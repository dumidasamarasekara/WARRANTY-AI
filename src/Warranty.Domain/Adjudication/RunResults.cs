using Warranty.Domain.Claims;
using Warranty.Domain.Policies;

namespace Warranty.Domain.Adjudication;

/// <summary>One FR-009 validation check result (e.g. <c>INVOICE_PRESENT</c>).</summary>
public sealed record ValidationCheck(string Check, bool Passed, string? Detail);

/// <summary>Deterministic cross-check of one field between the claim and a piece of evidence (FR-016).</summary>
public sealed record ConsistencyCheck(string Field, string? ClaimValue, string? EvidenceValue, bool Match);

/// <summary>Intake Agent output for a run: validation, structured extraction and missing items.</summary>
public sealed class IntakeResult
{
    private IntakeResult()
    {
        Validation = [];
        MissingItems = [];
        ExtractionJson = string.Empty;
    }

    public Guid RunId { get; private set; }

    public Guid TenantId { get; private set; }

    public IReadOnlyList<ValidationCheck> Validation { get; private set; }

    /// <summary>Output per intake-extraction.schema.json.</summary>
    public string ExtractionJson { get; private set; }

    public IReadOnlyList<RequestedItem> MissingItems { get; private set; }

    public bool IsComplete => Validation.All(v => v.Passed) && MissingItems.Count == 0;

    public static IntakeResult Create(Guid runId, Guid tenantId, IEnumerable<ValidationCheck> validation, string extractionJson, IEnumerable<RequestedItem> missingItems)
        => new()
        {
            RunId = runId,
            TenantId = tenantId,
            Validation = validation.ToArray(),
            ExtractionJson = extractionJson ?? "{}",
            MissingItems = missingItems.ToArray(),
        };
}

/// <summary>Evidence Agent finding for one evidence file (invoice extraction or photo analysis).</summary>
public sealed class EvidenceFinding
{
    private EvidenceFinding()
    {
        ResultJson = string.Empty;
        Consistency = [];
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid EvidenceId { get; private set; }

    public EvidenceFindingKind Kind { get; private set; }

    /// <summary>Output per invoice-extraction or photo-analysis schema.</summary>
    public string ResultJson { get; private set; }

    public IReadOnlyList<ConsistencyCheck> Consistency { get; private set; }

    /// <summary>The agent's confidence for photo analysis (0–100); null for invoice extraction.</summary>
    public int? Confidence { get; private set; }

    public static EvidenceFinding Create(
        Guid id, Guid tenantId, Guid runId, Guid evidenceId, EvidenceFindingKind kind, string resultJson,
        IEnumerable<ConsistencyCheck> consistency, int? confidence)
    {
        if (confidence is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be 0–100.");
        }

        return new EvidenceFinding
        {
            Id = id,
            TenantId = tenantId,
            RunId = runId,
            EvidenceId = evidenceId,
            Kind = kind,
            ResultJson = resultJson ?? "{}",
            Consistency = consistency.ToArray(),
            Confidence = confidence,
        };
    }
}

/// <summary>A policy clause retrieved for a run and issued as <c>POL-n</c> (FR-013).</summary>
public sealed class RetrievedPolicyRef
{
    private RetrievedPolicyRef()
    {
        RefId = ClauseKey = DocumentTitle = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RunId { get; private set; }

    /// <summary>Harness-issued ID, e.g. <c>POL-3</c>.</summary>
    public string RefId { get; private set; }

    public Guid KnowledgeChunkId { get; private set; }

    public Guid PolicyVersionId { get; private set; }

    public string ClauseKey { get; private set; }

    public ClauseType ClauseType { get; private set; }

    public ExclusionCode? ExclusionCode { get; private set; }

    public string DocumentTitle { get; private set; }

    public int Version { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public DateOnly? EffectiveTo { get; private set; }

    /// <summary>Similarity within the filtered set.</summary>
    public float Score { get; private set; }

    /// <summary>True when the recommendation cites this reference.</summary>
    public bool Cited { get; private set; }

    public static RetrievedPolicyRef Create(
        Guid id, Guid tenantId, Guid runId, string refId, Guid knowledgeChunkId, Guid policyVersionId, string clauseKey,
        ClauseType clauseType, ExclusionCode? exclusionCode, string documentTitle, int version, DateOnly effectiveFrom,
        DateOnly? effectiveTo, float score)
    {
        if (refId is null || !refId.StartsWith("POL-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Policy references are issued as POL-n.", nameof(refId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(clauseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentTitle);

        return new RetrievedPolicyRef
        {
            Id = id,
            TenantId = tenantId,
            RunId = runId,
            RefId = refId,
            KnowledgeChunkId = knowledgeChunkId,
            PolicyVersionId = policyVersionId,
            ClauseKey = clauseKey,
            ClauseType = clauseType,
            ExclusionCode = exclusionCode,
            DocumentTitle = documentTitle,
            Version = version,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            Score = score,
        };
    }

    public void MarkCited() => Cited = true;
}

/// <summary>Policy Agent output: version outcome plus the structured coverage reading.</summary>
public sealed class PolicyAssessment
{
    private PolicyAssessment()
    {
        AssessmentJson = Model = PromptVersion = string.Empty;
    }

    public Guid RunId { get; private set; }

    public Guid TenantId { get; private set; }

    public PolicyVersionOutcome VersionOutcome { get; private set; }

    /// <summary>Output per policy-assessment.schema.json.</summary>
    public string AssessmentJson { get; private set; }

    public int? Confidence { get; private set; }

    public string Model { get; private set; }

    public string PromptVersion { get; private set; }

    public static PolicyAssessment Create(
        Guid runId, Guid tenantId, PolicyVersionOutcome versionOutcome, string assessmentJson, int? confidence, string model, string promptVersion)
        => new()
        {
            RunId = runId,
            TenantId = tenantId,
            VersionOutcome = versionOutcome,
            AssessmentJson = assessmentJson ?? "{}",
            Confidence = confidence is < 0 or > 100
                ? throw new ArgumentOutOfRangeException(nameof(confidence))
                : confidence,
            Model = model ?? string.Empty,
            PromptVersion = promptVersion ?? string.Empty,
        };
}
