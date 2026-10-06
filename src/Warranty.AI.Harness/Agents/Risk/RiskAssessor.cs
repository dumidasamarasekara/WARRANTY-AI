using System.Globalization;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.AI.Harness.Agents.Risk;

/// <summary>
/// Computes a run's risk (FR-017, research R23/R25). A capability, not an LLM agent in the PoC; behind
/// an interface so it can become one later (contracts/agents-and-tools.md). A run stores exactly one
/// assessment (<c>adjudication.risk_assessments</c> is keyed by run): the runner calls
/// <see cref="AssessAtIntakeAsync"/> when the intake short-circuit applies, otherwise
/// <see cref="AssessFullAsync"/> once the Decision step has produced its signals. Both add the
/// assessment to <see cref="IAdjudicationRepository"/>; the caller commits the unit of work.
/// </summary>
public interface IRiskAssessor
{
    /// <summary>
    /// Signals available without evidence analysis (<c>PRODUCT_NOT_IN_CATALOG</c>,
    /// <c>DUPLICATE_SERIAL_CLAIM</c>, <c>EVIDENCE_REUSED</c> from upload hashes, <c>MANIPULATION_ATTEMPT</c>
    /// from the injection detector on the description and from the intake's <c>containsInstructionsToSystem</c>);
    /// stored with <see cref="RiskAssessmentStage.Intake"/>.
    /// </summary>
    Task<RiskAssessment> AssessAtIntakeAsync(CaseContext @case, IntakeResult intake, CancellationToken ct);

    /// <summary>
    /// The deterministic signals of the run so far (intake signals plus the evidence-dependent ones read
    /// from <see cref="AdjudicationContext.Evidence"/>), without storing anything. The Decision Agent's
    /// context lists them.
    /// </summary>
    Task<IReadOnlyList<RiskSignal>> DetectDeterministicSignalsAsync(AdjudicationContext run, CancellationToken ct);

    /// <summary>
    /// All deterministic signals (as <see cref="DetectDeterministicSignalsAsync"/>) plus the AI-reported
    /// ones (evidence and decision outputs, plus the intake's <c>containsInstructionsToSystem</c>); stored with <see cref="RiskAssessmentStage.Full"/> and set
    /// on <see cref="AdjudicationContext.Risk"/>.
    /// </summary>
    Task<RiskAssessment> AssessFullAsync(AdjudicationContext run, AiRiskReading ai, CancellationToken ct);
}

/// <summary>What the AI reported about risk: the model's own level (display only) and its signals.</summary>
/// <param name="ModelLevel">The decision output's <c>risk.level</c>; stored for display, never used for the computed level.</param>
/// <param name="Signals">AI-sourced signals from the evidence and decision outputs.</param>
public sealed record AiRiskReading(RiskLevel? ModelLevel, IReadOnlyList<RiskSignal> Signals)
{
    public static AiRiskReading None { get; } = new(null, []);
}

/// <summary>Result of the pure scoring rules: merged signals, score and level.</summary>
public sealed record RiskScore(int Score, RiskLevel Level, IReadOnlyList<RiskSignal> Signals);

/// <summary>A claim-vs-evidence check of one evidence file, with that file's <c>EV-n</c> reference when issued.</summary>
public sealed record EvidenceCheck(string? EvidenceRef, ConsistencyCheck Check);

/// <summary>
/// The evidence-dependent facts the full assessment reads: the <c>invoice_validation</c> checks
/// (field names from <see cref="InvoiceFieldNames"/>) and the serial-in-photo comparisons. Only
/// compared fields appear: a value the evidence does not show (an unreadable invoice field, a photo
/// without a visible serial) has no check and never raises a signal.
/// </summary>
public sealed record EvidenceRiskFacts(IReadOnlyList<EvidenceCheck> InvoiceChecks, IReadOnlyList<EvidenceCheck> PhotoSerialChecks)
{
    public static EvidenceRiskFacts None { get; } = new([], []);

    /// <summary>
    /// Reads the Evidence step output: an invoice finding's consistency checks are invoice checks; a
    /// photo finding's <c>serialNumber</c> check (claimed serial vs the serial visible in the photo) is a
    /// photo serial check. Checks listed only in <see cref="EvidenceResult.ConsistencyChecks"/> count as
    /// invoice checks without a reference.
    /// </summary>
    public static EvidenceRiskFacts From(EvidenceResult? evidence, ReferenceRegistry references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (evidence is null)
        {
            return None;
        }

        var refsByEvidence = references.Entries
            .Where(e => e.Kind == ReferenceKind.Evidence)
            .ToDictionary(e => e.TargetId, e => e.Id);
        var invoice = new List<EvidenceCheck>();
        var photos = new List<EvidenceCheck>();
        foreach (var finding in evidence.Findings)
        {
            var reference = refsByEvidence.GetValueOrDefault(finding.EvidenceId);
            foreach (var check in finding.Consistency)
            {
                if (finding.Kind == EvidenceFindingKind.InvoiceExtraction)
                {
                    invoice.Add(new(reference, check));
                }
                else if (string.Equals(check.Field, InvoiceFieldNames.SerialNumber, StringComparison.Ordinal))
                {
                    photos.Add(new(reference, check));
                }
            }
        }

        var perFinding = evidence.Findings.SelectMany(f => f.Consistency).ToHashSet();
        invoice.AddRange(evidence.ConsistencyChecks.Where(c => !perFinding.Contains(c)).Select(c => new EvidenceCheck(null, c)));
        return new(invoice, photos);
    }
}

/// <summary>Deterministic risk assessment (research R23).</summary>
public sealed class RiskAssessor(
    ClaimHistoryLookupTool history,
    InjectionDetector injection,
    ITenantRepository tenants,
    IAdjudicationRepository adjudication,
    TimeProvider time) : IRiskAssessor
{
    private const int MaxScore = 100;

    /// <summary>Invoice fields whose mismatch is a <c>SOURCE_INCONSISTENCY</c>; the purchase date is a <c>PURCHASE_DATE_ANOMALY</c>.</summary>
    private static readonly HashSet<string> SourceFields = new(StringComparer.Ordinal)
    {
        InvoiceFieldNames.SerialNumber, InvoiceFieldNames.ModelCode, InvoiceFieldNames.PurchasePrice, InvoiceFieldNames.Seller,
    };

    /// <summary>Score weight of a severity: <c>Low</c> 10, <c>Medium</c> 25, <c>High</c> 40.</summary>
    public static int WeightOf(RiskSeverity severity) => severity switch
    {
        RiskSeverity.Low => 10,
        RiskSeverity.Medium => 25,
        RiskSeverity.High => 40,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown risk severity."),
    };

    /// <summary>
    /// Fixed severity of a deterministic signal code (<c>MANIPULATION_ATTEMPT</c>, <c>EVIDENCE_REUSED</c>,
    /// <c>SERIAL_MISMATCH_PHOTO</c> High; <c>DUPLICATE_SERIAL_CLAIM</c>, <c>PRODUCT_NOT_IN_CATALOG</c>,
    /// <c>SOURCE_INCONSISTENCY</c>, <c>PURCHASE_DATE_ANOMALY</c> Medium); AI-sourced signals are Medium.
    /// </summary>
    public static RiskSeverity SeverityOf(RiskSignalCode code, RiskSignalSource source)
        => source == RiskSignalSource.Deterministic
           && code is RiskSignalCode.ManipulationAttempt or RiskSignalCode.EvidenceReused or RiskSignalCode.SerialMismatchPhoto
            ? RiskSeverity.High
            : RiskSeverity.Medium;

    /// <summary><c>Low</c> only without any signal; otherwise <c>High</c> when the score reaches the threshold, else <c>Medium</c>.</summary>
    public static RiskLevel LevelFor(int score, bool anySignal, int riskHighThreshold)
    {
        if (!anySignal)
        {
            return RiskLevel.Low;
        }

        return score >= riskHighThreshold ? RiskLevel.High : RiskLevel.Medium;
    }

    /// <summary>
    /// Pure scoring: the union of deterministic and AI signals (a code raised by both counted once with
    /// the deterministic severity), score = <c>min(100, Σ weight)</c>, level per <see cref="LevelFor"/>.
    /// Every signal carries its fixed severity (<see cref="SeverityOf"/>) whatever severity it arrived
    /// with; a code is counted once per source. The AI's <see cref="AiRiskReading.ModelLevel"/> never
    /// changes the result.
    /// </summary>
    public static RiskScore Compute(IReadOnlyList<RiskSignal> deterministicSignals, AiRiskReading ai, int riskHighThreshold)
    {
        ArgumentNullException.ThrowIfNull(deterministicSignals);
        ArgumentNullException.ThrowIfNull(ai);

        var merged = new List<RiskSignal>();
        var byCode = new Dictionary<RiskSignalCode, int>();
        void Add(RiskSignal signal, RiskSignalSource source)
        {
            if (byCode.TryGetValue(signal.Code, out var index))
            {
                // Same code from the same source: keep one signal, with the references of both.
                if (merged[index].Source == source)
                {
                    merged[index] = merged[index] with { EvidenceRefs = merged[index].EvidenceRefs.Union(signal.EvidenceRefs, StringComparer.Ordinal).ToArray() };
                }

                return;
            }

            byCode[signal.Code] = merged.Count;
            merged.Add(signal with { Source = source, Severity = SeverityOf(signal.Code, source) });
        }

        foreach (var signal in deterministicSignals)
        {
            Add(signal, RiskSignalSource.Deterministic);
        }

        foreach (var signal in ai.Signals)
        {
            Add(signal, RiskSignalSource.Ai);
        }

        var score = Math.Min(MaxScore, merged.Sum(s => WeightOf(s.Severity)));
        return new RiskScore(score, LevelFor(score, merged.Count > 0, riskHighThreshold), merged);
    }

    /// <summary>
    /// Signals known before evidence analysis: <c>PRODUCT_NOT_IN_CATALOG</c> when the model/serial pair
    /// is not in the tenant's catalog, <c>DUPLICATE_SERIAL_CLAIM</c> and <c>EVIDENCE_REUSED</c> from the
    /// same-tenant history counts (research R25).
    /// </summary>
    public static IReadOnlyList<RiskSignal> IntakeSignals(CaseContext @case, ClaimHistoryCounts history)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(history);

        var signals = new List<RiskSignal>();
        if (@case.Product is null)
        {
            // The case's product is set only when the serial is registered to the claimed model in this tenant's
            // catalog (CaseKnowledgeProvider); a known model with an unknown serial lands here too (FR-003).
            signals.Add(Deterministic(
                RiskSignalCode.ProductNotInCatalog,
                "The product model and serial number are not in the tenant's catalog; no claim value can be determined."));
        }

        if (history.DuplicateClaimsForSerial > 0)
        {
            signals.Add(Deterministic(
                RiskSignalCode.DuplicateSerialClaim,
                Invariant($"{history.DuplicateClaimsForSerial} other claim(s) for this serial number are open or were finalized within the last 90 days.")));
        }

        if (history.EvidenceReuseMatches > 0)
        {
            signals.Add(Deterministic(
                RiskSignalCode.EvidenceReused,
                Invariant($"{history.EvidenceReuseMatches} evidence file(s) of other claims are identical to files of this claim.")));
        }

        return signals;
    }

    /// <summary>
    /// Signals from evidence analysis: <c>PURCHASE_DATE_ANOMALY</c> when the invoice date differs from
    /// the stated purchase date or lies after the claim date or <paramref name="today"/> (the stated date
    /// itself is validated at submission and never raises it); <c>SOURCE_INCONSISTENCY</c> when the
    /// invoice's serial, model, price or seller check failed; <c>SERIAL_MISMATCH_PHOTO</c> when a serial
    /// visible in a photo differs from the claimed one. Fields that were not compared raise nothing.
    /// </summary>
    public static IReadOnlyList<RiskSignal> EvidenceSignals(CaseContext @case, EvidenceRiskFacts evidence, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(evidence);

        var signals = new List<RiskSignal>();
        var dateChecks = evidence.InvoiceChecks
            .Where(c => string.Equals(c.Check.Field, InvoiceFieldNames.PurchaseDate, StringComparison.Ordinal))
            .Where(c => IsDateAnomaly(c.Check, @case.ClaimDate, today))
            .ToList();
        if (dateChecks.Count > 0)
        {
            signals.Add(Deterministic(
                RiskSignalCode.PurchaseDateAnomaly,
                "The invoice date differs from the stated purchase date or lies after the claim date.",
                dateChecks));
        }

        var sourceChecks = evidence.InvoiceChecks
            .Where(c => !c.Check.Match && SourceFields.Contains(c.Check.Field))
            .ToList();
        if (sourceChecks.Count > 0)
        {
            var fields = string.Join(", ", sourceChecks.Select(c => c.Check.Field).Distinct(StringComparer.Ordinal));
            signals.Add(Deterministic(
                RiskSignalCode.SourceInconsistency,
                $"The invoice does not match the claim on: {fields}.",
                sourceChecks));
        }

        var photoChecks = evidence.PhotoSerialChecks.Where(c => !c.Check.Match).ToList();
        if (photoChecks.Count > 0)
        {
            signals.Add(Deterministic(
                RiskSignalCode.SerialMismatchPhoto,
                "The serial number visible in a photo differs from the claimed serial number.",
                photoChecks));
        }

        return signals;
    }

    /// <summary>
    /// The claimant-supplied text the injection detector screens (research R14): the problem description,
    /// the text extracted from each invoice and the observations read from each photo. Evidence parts are
    /// labelled with the file's issued <c>EV-n</c>, or with its evidence ID when none was issued.
    /// </summary>
    public static IReadOnlyList<UntrustedTextPart> ClaimantTexts(CaseContext @case, EvidenceResult? evidence, ReferenceRegistry? references)
    {
        ArgumentNullException.ThrowIfNull(@case);

        var parts = new List<UntrustedTextPart> { UntrustedContent.ClaimantDescription(@case.ProblemDescription ?? string.Empty) };
        if (evidence is null)
        {
            return parts;
        }

        var refs = (references?.Entries ?? [])
            .Where(e => e.Kind == ReferenceKind.Evidence)
            .ToDictionary(e => e.TargetId, e => e.Id);
        foreach (var finding in evidence.Findings)
        {
            var reference = refs.GetValueOrDefault(finding.EvidenceId) ?? finding.EvidenceId.ToString("D", CultureInfo.InvariantCulture);
            if (InvoiceExtractionOutput.From(finding) is { } invoice)
            {
                // One part per field, so a phrase is never assembled from the end of one field and the start of the next.
                string[] fields = [invoice.SellerName, invoice.InvoiceNumber, invoice.ProductDescription, invoice.ModelCodeOnInvoice, invoice.SerialOnInvoice, .. invoice.Anomalies];
                parts.AddRange(fields.Select(text => UntrustedContent.InvoiceText(reference, text ?? string.Empty)));
            }
            else if (PhotoAnalysisOutput.From(finding) is { } photo)
            {
                parts.Add(UntrustedContent.ImageText(reference, photo.Observations));
            }
        }

        return parts;
    }

    /// <summary>
    /// A deterministic <c>MANIPULATION_ATTEMPT</c> when the injection detector found a listed phrase, naming
    /// where it was found (with the <c>EV-n</c> of the evidence files); the phrases themselves are not
    /// repeated, since the list is confidential.
    /// </summary>
    public static IReadOnlyList<RiskSignal> ManipulationSignals(IReadOnlyList<InjectionMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
        {
            return [];
        }

        var sources = matches.Select(m => m.Source).Distinct(StringComparer.Ordinal).ToList();
        var refs = sources
            .Select(s => s[(s.IndexOf(' ', StringComparison.Ordinal) + 1)..])
            .Where(s => s.StartsWith("EV-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var signal = Deterministic(
            RiskSignalCode.ManipulationAttempt,
            $"Claimant-supplied text contains instructions addressed to the adjudication system ({string.Join(", ", sources)}).");
        return [signal with { EvidenceRefs = refs }];
    }

    /// <summary>An AI-sourced <c>MANIPULATION_ATTEMPT</c> when the Intake step reported <c>containsInstructionsToSystem</c>.</summary>
    public static IReadOnlyList<RiskSignal> IntakeAiSignals(IntakeResult? intake)
        => intake is not null && IntakeExtraction.From(intake) is { ContainsInstructionsToSystem: true }
            ? [new RiskSignal(
                RiskSignalCode.ManipulationAttempt, RiskSignalSource.Ai, SeverityOf(RiskSignalCode.ManipulationAttempt, RiskSignalSource.Ai),
                "The problem description contains text addressed to the adjudication system.", [])]
            : [];

    public async Task<RiskAssessment> AssessAtIntakeAsync(CaseContext @case, IntakeResult intake, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(intake);

        IReadOnlyList<RiskSignal> signals =
        [
            .. IntakeSignals(@case, await history.LookupAsync(@case.ClaimId, ct)),
            .. ManipulationSignals(injection.Detect(ClaimantTexts(@case, null, null))),
        ];
        var settings = await tenants.GetCurrentSettingsAsync(ct);
        var score = Compute(signals, new AiRiskReading(null, IntakeAiSignals(intake)), settings.RiskHighThreshold);
        var assessment = RiskAssessment.Create(intake.RunId, intake.TenantId, RiskAssessmentStage.Intake, score.Score, score.Level, score.Signals);
        adjudication.AddRiskAssessment(assessment);
        return assessment;
    }

    public async Task<IReadOnlyList<RiskSignal>> DetectDeterministicSignalsAsync(AdjudicationContext run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        var counts = await history.LookupAsync(run.ClaimId, ct);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        return
        [
            .. IntakeSignals(run.Case, counts),
            .. EvidenceSignals(run.Case, EvidenceRiskFacts.From(run.Evidence, run.References), today),
            .. ManipulationSignals(injection.Detect(ClaimantTexts(run.Case, run.Evidence, run.References))),
        ];
    }

    public async Task<RiskAssessment> AssessFullAsync(AdjudicationContext run, AiRiskReading ai, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(ai);

        var signals = await DetectDeterministicSignalsAsync(run, ct);
        var settings = await tenants.GetCurrentSettingsAsync(ct);
        var score = Compute(signals, ai with { Signals = [.. ai.Signals, .. IntakeAiSignals(run.Intake)] }, settings.RiskHighThreshold);
        var assessment = RiskAssessment.Create(run.RunId, run.Tenant.TenantId, RiskAssessmentStage.Full, score.Score, score.Level, score.Signals);
        adjudication.AddRiskAssessment(assessment);
        run.Risk = assessment;
        return assessment;
    }

    private static bool IsDateAnomaly(ConsistencyCheck check, DateOnly claimDate, DateOnly today)
    {
        if (!check.Match)
        {
            return true;
        }

        return DateOnly.TryParseExact(check.EvidenceValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var invoiceDate)
               && (invoiceDate > claimDate || invoiceDate > today);
    }

    private static RiskSignal Deterministic(RiskSignalCode code, string detail, IEnumerable<EvidenceCheck>? checks = null)
        => new(
            code,
            RiskSignalSource.Deterministic,
            SeverityOf(code, RiskSignalSource.Deterministic),
            detail,
            (checks ?? []).Select(c => c.EvidenceRef).OfType<string>().Distinct(StringComparer.Ordinal).ToArray());

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
