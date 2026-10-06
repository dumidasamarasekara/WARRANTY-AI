using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Domain.Tenancy;
using Warranty.Guardrails;
using Warranty.Guardrails.Pipeline;
using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// A guardrail input that starts as a clear-cut claim satisfying every FR-026 condition (or, with
/// <see cref="ClearReject"/>, every FR-027 condition on an expired-period ground); tests flip one
/// fact at a time and evaluate it with <see cref="Evaluate"/>.
/// </summary>
internal sealed class GuardrailScenario
{
    public const string InvoiceRef = "EV-1";
    public const string PhotoRef = "EV-2";
    public const string CoverageClause = "POL-1";
    public const string PeriodClause = "POL-2";
    public const string AccidentalExclusion = "POL-3";
    public const string LiquidExclusion = "POL-4";
    public const string UnauthorizedRepairExclusion = "POL-5";
    public const string OtherVersionAccidentalExclusion = "POL-6";

    /// <summary>A cosmetic exclusion clause whose code the applicable version does not list in <c>terms.exclusions</c>.</summary>
    public const string UnlistedCosmeticExclusion = "POL-7";
    public const string SafeApproveText = "Your tablet is covered for manufacturing defects for 12 months and will be repaired.";
    public const string SafeRejectText = "Your tablet's warranty period ended before the claim date, so this repair is not covered.";

    public static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private static readonly DateOnly ClaimDay = new(2026, 9, 1);

    private GuardrailScenario(AccidentalDamageTerms? accidentalDamage = null)
    {
        accidentalDamage ??= AccidentalDamageTerms.NotCovered;
        Version = Policy(
            Guid.Parse("22222222-2222-7222-8222-222222222222"), 2,
            accidentalDamage.Covered
                ? [ExclusionCode.LiquidDamage, ExclusionCode.UnauthorizedRepair]
                : [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage, ExclusionCode.UnauthorizedRepair],
            accidentalDamage);
        var otherVersion = Policy(Guid.Parse("33333333-3333-7333-8333-333333333333"), 1, [ExclusionCode.AccidentalDamage]);
        Clauses =
        [
            Clause(CoverageClause, Version, ClauseType.Coverage, null),
            Clause(PeriodClause, Version, ClauseType.Period, null),
            Clause(AccidentalExclusion, Version, ClauseType.Exclusion, ExclusionCode.AccidentalDamage),
            Clause(LiquidExclusion, Version, ClauseType.Exclusion, ExclusionCode.LiquidDamage),
            Clause(UnauthorizedRepairExclusion, Version, ClauseType.Exclusion, ExclusionCode.UnauthorizedRepair),
            Clause(OtherVersionAccidentalExclusion, otherVersion, ClauseType.Exclusion, ExclusionCode.AccidentalDamage),
            Clause(UnlistedCosmeticExclusion, Version, ClauseType.Exclusion, ExclusionCode.CosmeticDamage),
        ];
    }

    public Guid ClaimId { get; } = Guid.Parse("44444444-4444-7444-8444-444444444444");

    public Guid RunId { get; } = Guid.Parse("55555555-5555-7555-8555-555555555555");

    // Tenant settings
    public decimal AutoApprovalLimit { get; set; } = 1_000m;

    public int MinConfidence { get; set; } = 80;

    public bool AutoApproveEnabled { get; set; } = true;

    public bool AutoRejectEnabled { get; set; } = true;

    public List<string> AlwaysReviewCategories { get; } = ["oven"];

    // Case facts
    public bool ProductInCatalog { get; set; } = true;

    public string? ProductCategory { get; set; } = "tablet";

    public decimal? ClaimValue { get; set; } = 800m;

    public bool ReviewerInfoRequested { get; set; }

    public int AutoInfoRequestCount { get; set; }

    public DateOnly PurchaseDate { get; set; } = new(2026, 1, 15);

    /// <summary>All-time approved accidental-damage claims for the serial (<c>claim_history_lookup</c>).</summary>
    public int PriorApprovedAccidental { get; set; }

    public List<RequestedItem> IntakeMissingItems { get; } = [];

    // Evidence
    public List<ConsistencyCheck> ConsistencyChecks { get; } =
    [
        new("serialNumber", "SN-1001", "SN-1001", true),
        new("purchaseDate", "2026-01-15", "2026-01-15", true),
    ];

    public List<string> PhotoDamageTypes { get; } = ["OTHER"];

    // Policy
    public PolicyVersionOutcome VersionOutcome { get; set; } = PolicyVersionOutcome.Ok;

    public PolicyVersion Version { get; }

    /// <summary>The Policy agent's <c>ambiguity.isAmbiguous</c> flag.</summary>
    public bool PolicyAgentReportsAmbiguity { get; set; }

    public IReadOnlyList<RetrievedPolicyRef> Clauses { get; }

    /// <summary>Deterministic coverage window (the calculator's result is built from these two values).</summary>
    public DateOnly CoverageEndDate { get; set; } = new(2027, 1, 15);

    public bool WithinCoverageWindow { get; set; } = true;

    // Risk (Low means no signal of any kind, research R23)
    public List<RiskSignal> RiskSignals { get; } = [];

    /// <summary>The computed level when at least one signal is present (any signal makes risk at least Medium).</summary>
    public RiskLevel SignalledRiskLevel { get; set; } = RiskLevel.Medium;

    // AI recommendation; null means AI analysis could not be completed (FR-031)
    public bool AiAvailable { get; set; } = true;

    public bool RecommendationValid { get; set; } = true;

    public AiDecision Decision { get; set; } = AiDecision.Approve;

    public CoverageDetermination Coverage { get; set; } = CoverageDetermination.Covered;

    public int Confidence { get; set; } = 92;

    public string ClaimantExplanation { get; set; } = SafeApproveText;

    public List<EvidenceCitation> EvidenceCitations { get; } =
    [
        new(InvoiceRef, "Invoice matches the claimed model, serial and purchase date."),
        new(PhotoRef, "Screen shows dead pixels without impact marks."),
    ];

    public List<PolicyCitation> PolicyCitations { get; } =
    [
        new(CoverageClause, PolicyRefRelevance.SupportsCoverage),
        new(PeriodClause, PolicyRefRelevance.DefinesPeriod),
    ];

    /// <summary>The recommendation's <c>missingInformation</c> items.</summary>
    public List<RequestedItem> AiMissingInformation { get; } = [];

    public bool ManipulationDetected { get; set; }

    /// <summary>The model's own risk level, recorded in the raw output but never used by the guardrails (R23).</summary>
    public string AiReportedRiskLevel { get; set; } = "LOW";

    public List<string> IssuedReferences { get; } =
        [InvoiceRef, PhotoRef, CoverageClause, PeriodClause, AccidentalExclusion, LiquidExclusion, UnauthorizedRepairExclusion, OtherVersionAccidentalExclusion,
            UnlistedCosmeticExclusion];

    public ActorInfo Actor { get; set; } = ActorInfo.Automation;

    /// <summary>A defect claim within the coverage window that the AI approves: every FR-026 condition holds.</summary>
    public static GuardrailScenario ClearApprove() => new();

    /// <summary>
    /// Tenant B's allowance (one accidental-damage incident within 12 months, accidental damage not excluded):
    /// a cracked screen bought 10 months before the claim date that the AI approves on the coverage clause.
    /// </summary>
    public static GuardrailScenario ApproveAccidentalDamage()
    {
        var scenario = new GuardrailScenario(new AccidentalDamageTerms(true, 12, 1))
        {
            PurchaseDate = new DateOnly(2025, 11, 1),
            ClaimantExplanation = "Your plan includes one accidental-damage repair in the first 12 months, so your screen will be repaired.",
        };
        scenario.PhotoDamageTypes.Clear();
        scenario.PhotoDamageTypes.Add("CRACKED_SCREEN");
        return scenario;
    }

    /// <summary>A claim made after the coverage window ended that the AI rejects on the period clause: every FR-027 condition holds.</summary>
    public static GuardrailScenario ClearReject()
    {
        var scenario = new GuardrailScenario
        {
            Decision = AiDecision.Reject,
            Coverage = CoverageDetermination.NotCovered,
            ClaimantExplanation = SafeRejectText,
            CoverageEndDate = new DateOnly(2025, 6, 1),
            WithinCoverageWindow = false,
        };
        scenario.PolicyCitations.Clear();
        scenario.PolicyCitations.Add(new PolicyCitation(PeriodClause, PolicyRefRelevance.SupportsRejection));
        return scenario;
    }

    /// <summary>The AI rejects on one exclusion clause; the period ground is not cited and the claim is inside the window.</summary>
    public static GuardrailScenario RejectOnExclusion(string exclusionClause, params string[] photoDamageTypes)
    {
        var scenario = ClearReject();
        scenario.CoverageEndDate = new DateOnly(2027, 1, 15);
        scenario.WithinCoverageWindow = true;
        scenario.ClaimantExplanation = "Your tablet's screen damage is excluded from the warranty, so this repair is not covered.";
        scenario.PolicyCitations.Clear();
        scenario.PolicyCitations.Add(new PolicyCitation(exclusionClause, PolicyRefRelevance.SupportsRejection));
        scenario.PhotoDamageTypes.Clear();
        scenario.PhotoDamageTypes.AddRange(photoDamageTypes);
        return scenario;
    }

    public void AddRiskSignal(RiskSignalCode code, RiskSignalSource source)
        => RiskSignals.Add(new RiskSignal(code, source, RiskSeverity.Medium, $"{code} raised for the test.", []));

    public GuardrailInput Build()
    {
        var settings = TenantSettings.Create(
            Tenant, "EUR", AutoApprovalLimit, MinConfidence, AutoApproveEnabled, AutoRejectEnabled, AlwaysReviewCategories);
        var caseFacts = new CaseFacts(
            Tenant, ClaimId, RunId, ProductInCatalog, ProductCategory, ClaimValue, ReviewerInfoRequested, AutoInfoRequestCount,
            PurchaseDate, PriorApprovedAccidental);
        var intake = IntakeResult.Create(
            RunId, Tenant, [new ValidationCheck("INVOICE_PRESENT", true, null), new ValidationCheck("PHOTO_PRESENT", true, null)],
            "{}", IntakeMissingItems);
        var evidence = new EvidenceFacts(ConsistencyChecks.ToArray(), [new PhotoFinding(PhotoRef, PhotoDamageTypes.ToArray())], []);
        var window = VersionOutcome == PolicyVersionOutcome.Ok
            ? new CoverageWindowResult(CoverageWindowOutcome.Determined, CoverageEndDate, WithinCoverageWindow, WithinCoverageWindow)
            : new CoverageWindowResult(CoverageWindowOutcome.NoApplicablePolicy, null, null, null);
        var policy = new PolicyFacts(VersionOutcome, VersionOutcome == PolicyVersionOutcome.Ok ? Version : null, Clauses, window, PolicyAgentReportsAmbiguity);
        var risk = RiskAssessment.Create(
            RunId, Tenant, RiskAssessmentStage.Full, Math.Min(100, 25 * RiskSignals.Count),
            RiskSignals.Count == 0 ? RiskLevel.Low : SignalledRiskLevel, RiskSignals);

        return new GuardrailInput(
            settings, caseFacts, intake, evidence, policy, risk, AiAvailable ? BuildRecommendation() : null,
            IssuedReferences.ToHashSet(StringComparer.Ordinal), Actor, ClaimDay);
    }

    public GuardrailOutcome Evaluate() => new GuardrailEngine().Evaluate(Build());

    private Recommendation BuildRecommendation()
    {
        var raw = $$$"""{"decision":"{{{WireName.Of(Decision)}}}","confidence":{{{Confidence}}},"risk":{"level":"{{{AiReportedRiskLevel}}}","signals":[]}}""";
        return RecommendationValid
            ? Recommendation.CreateValid(
                RunId, Tenant, raw, Decision, Coverage, Confidence, "Staff-facing reasoning summary.", ClaimantExplanation,
                EvidenceCitations, PolicyCitations, AiMissingInformation, ManipulationDetected, "claude-opus-5-5", "decision", "v1")
            : Recommendation.CreateInvalid(
                RunId, Tenant, raw, ["$.policyRefs: required"], "claude-opus-5-5", "decision", "v1", Decision, Confidence);
    }

    private static PolicyVersion Policy(
        Guid id, int version, IReadOnlyList<ExclusionCode> exclusions, AccidentalDamageTerms? accidentalDamage = null) => PolicyVersion.Create(
        id, Tenant, Guid.Parse("66666666-6666-7666-8666-666666666666"), version,
        new DateOnly(2024, 1, 1), null, [Region.NA, Region.EU], [],
        new CoverageTerms(
            new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 },
            new Dictionary<string, int> { ["battery"] = 6 },
            accidentalDamage ?? AccidentalDamageTerms.NotCovered,
            exclusions),
        $"tenant-a/warranty-v{version}.md", $"sha256-v{version}");

    private RetrievedPolicyRef Clause(string refId, PolicyVersion version, ClauseType type, ExclusionCode? code) => RetrievedPolicyRef.Create(
        Guid.CreateVersion7(), Tenant, RunId, refId, Guid.CreateVersion7(), version.Id, $"AUR-WP-{refId}", type, code,
        "Aurora Warranty Policy", version.Version, version.EffectiveFrom, version.EffectiveTo, 0.82f);
}
