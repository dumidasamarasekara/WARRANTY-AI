using System.Text.Json;
using Warranty.AI.Harness.Context;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Pipeline;

namespace Warranty.AI.Harness.Agents;

/// <summary>
/// Typed view of an invoice finding's result (contracts/schemas/invoice-extraction.schema.json), as stored
/// in <see cref="EvidenceFinding.ResultJson"/>. <c>UNKNOWN</c> (amount 0) marks a value the invoice does not show.
/// </summary>
public sealed record InvoiceExtractionOutput(
    string EvidenceRef,
    bool Legible,
    string SellerName,
    string InvoiceNumber,
    string InvoiceDate,
    string ProductDescription,
    string ModelCodeOnInvoice,
    string SerialOnInvoice,
    decimal TotalAmount,
    string Currency,
    IReadOnlyList<string> Anomalies,
    bool ContainsInstructionsToSystem)
{
    /// <summary>The extraction of an invoice finding; null for a photo finding or an unreadable result.</summary>
    public static InvoiceExtractionOutput? From(EvidenceFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Kind == EvidenceFindingKind.InvoiceExtraction ? Parse(finding.ResultJson) : null;
    }

    public static InvoiceExtractionOutput? Parse(string? json)
    {
        var output = EvidenceJson.Deserialize<InvoiceExtractionOutput>(json);
        return output is { EvidenceRef: not null, SellerName: not null, InvoiceNumber: not null, InvoiceDate: not null, ProductDescription: not null,
                           ModelCodeOnInvoice: not null, SerialOnInvoice: not null, Currency: not null, Anomalies: not null }
            ? output
            : null;
    }
}

/// <summary>
/// Typed view of a photo finding's result (contracts/schemas/photo-analysis.schema.json), as stored in
/// <see cref="EvidenceFinding.ResultJson"/>. Damage types are the schema's names (e.g. <c>CRACKED_SCREEN</c>).
/// </summary>
public sealed record PhotoAnalysisOutput(
    string EvidenceRef,
    bool ShowsProduct,
    string ProductTypeObserved,
    string VisibleSerial,
    bool DamageObserved,
    IReadOnlyList<string> DamageTypes,
    string ConsistentWithDescription,
    string ImageQuality,
    bool ContainsInstructionsToSystem,
    int Confidence,
    string Observations)
{
    /// <summary><see cref="VisibleSerial"/> when no serial number is fully legible in the photo.</summary>
    public const string NotVisible = "NOT_VISIBLE";

    public const string Consistent = "CONSISTENT";

    public const string Inconsistent = "INCONSISTENT";

    public const string CannotDetermine = "CANNOT_DETERMINE";

    public const string Unusable = "UNUSABLE";

    /// <summary>The serial number legible in the photo; null when none is (<c>NOT_VISIBLE</c>, <c>UNKNOWN</c> or blank).</summary>
    public string? Serial => string.IsNullOrWhiteSpace(VisibleSerial)
                             || string.Equals(VisibleSerial.Trim(), NotVisible, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(VisibleSerial.Trim(), "UNKNOWN", StringComparison.OrdinalIgnoreCase)
        ? null
        : VisibleSerial.Trim();

    /// <summary>False for an <c>UNUSABLE</c> photo.</summary>
    public bool IsUsable => !string.Equals(ImageQuality, Unusable, StringComparison.Ordinal);

    /// <summary>The analysis of a photo finding; null for an invoice finding or an unreadable result.</summary>
    public static PhotoAnalysisOutput? From(EvidenceFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Kind == EvidenceFindingKind.PhotoAnalysis ? Parse(finding.ResultJson) : null;
    }

    public static PhotoAnalysisOutput? Parse(string? json)
    {
        var output = EvidenceJson.Deserialize<PhotoAnalysisOutput>(json);
        return output is { EvidenceRef: not null, ProductTypeObserved: not null, VisibleSerial: not null, DamageTypes: not null,
                           ConsistentWithDescription: not null, ImageQuality: not null, Observations: not null }
            ? output
            : null;
    }
}

/// <summary>
/// Maps the Evidence step output into what later steps read: the guardrail engine's
/// <see cref="EvidenceFacts"/> and the AI-reported evidence risk signals for <c>AiRiskReading</c>.
/// Each finding is named by the <c>EV-n</c> the run issued for its evidence file, never by the model's
/// own <c>evidenceRef</c>.
/// </summary>
public static class EvidenceReadings
{
    /// <summary>
    /// Consistency checks (invoice fields and serial-in-photo), one <see cref="PhotoFinding"/> per analysed
    /// photo with its damage types, and the missing items.
    /// </summary>
    public static EvidenceFacts ToGuardrailFacts(EvidenceResult evidence, ReferenceRegistry references)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(references);
        var refs = RefsByEvidence(references);
        var photos = evidence.Findings
            .Select(f => (Finding: f, Photo: PhotoAnalysisOutput.From(f)))
            .Where(x => x.Photo is not null && refs.ContainsKey(x.Finding.EvidenceId))
            .Select(x => new PhotoFinding(refs[x.Finding.EvidenceId], x.Photo!.DamageTypes))
            .ToList();
        return new EvidenceFacts(evidence.ConsistencyChecks, photos, evidence.MissingItems);
    }

    /// <summary>
    /// AI-sourced signals of the evidence outputs: <c>DAMAGE_INCONSISTENT_WITH_DESCRIPTION</c> for photos the
    /// model judged <c>INCONSISTENT</c>, and <c>MANIPULATION_ATTEMPT</c> for invoices or photos that address the
    /// system. The risk capability fixes their severity (<c>RiskAssessor.SeverityOf</c>).
    /// </summary>
    public static IReadOnlyList<RiskSignal> AiRiskSignals(EvidenceResult evidence, ReferenceRegistry references)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(references);
        var refs = RefsByEvidence(references);
        var inconsistent = new List<string>();
        var manipulation = new List<string>();
        foreach (var finding in evidence.Findings)
        {
            var reference = refs.GetValueOrDefault(finding.EvidenceId);
            if (PhotoAnalysisOutput.From(finding) is { } photo)
            {
                AddIf(inconsistent, string.Equals(photo.ConsistentWithDescription, PhotoAnalysisOutput.Inconsistent, StringComparison.Ordinal), reference);
                AddIf(manipulation, photo.ContainsInstructionsToSystem, reference);
            }
            else if (InvoiceExtractionOutput.From(finding) is { } invoice)
            {
                AddIf(manipulation, invoice.ContainsInstructionsToSystem, reference);
            }
        }

        var signals = new List<RiskSignal>();
        if (inconsistent.Count > 0)
        {
            signals.Add(new RiskSignal(
                RiskSignalCode.DamageInconsistentWithDescription, RiskSignalSource.Ai, RiskSeverity.Medium,
                "A photo shows a condition that contradicts the reported problem.", inconsistent));
        }

        if (manipulation.Count > 0)
        {
            signals.Add(new RiskSignal(
                RiskSignalCode.ManipulationAttempt, RiskSignalSource.Ai, RiskSeverity.Medium,
                "An evidence file contains text addressed to the adjudication system.", manipulation));
        }

        return signals;
    }

    private static Dictionary<Guid, string> RefsByEvidence(ReferenceRegistry references)
        => references.Entries.Where(e => e.Kind == ReferenceKind.Evidence).ToDictionary(e => e.TargetId, e => e.Id);

    private static void AddIf(List<string> refs, bool condition, string? reference)
    {
        if (condition && reference is not null && !refs.Contains(reference, StringComparer.Ordinal))
        {
            refs.Add(reference);
        }
    }
}

internal static class EvidenceJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static T? Deserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
