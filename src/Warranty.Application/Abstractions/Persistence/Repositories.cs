using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Adjudication;
using Warranty.Domain.AiOps;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Integration;
using Warranty.Domain.Policies;
using Warranty.Domain.Review;
using Warranty.Domain.Tenancy;

namespace Warranty.Application.Abstractions.Persistence;

// Repositories operate on the current tenant: EF query filters and PostgreSQL RLS scope every query
// to ITenantContext (research R8). Only the platform-table lookups in ITenantRepository take IDs or
// host names, because they run before a tenant is resolved.

/// <summary>Commits the current unit of work in one transaction.</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);

    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct);
}

/// <summary>Platform-level tenant lookups used during tenant resolution.</summary>
public interface ITenantRepository
{
    Task<Tenant?> GetActiveTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Maps a normalized claimant-channel host to its active tenant.</summary>
    Task<Tenant?> FindByChannelHostAsync(string hostname, CancellationToken ct);

    /// <summary>Settings of the current tenant.</summary>
    Task<TenantSettings> GetCurrentSettingsAsync(CancellationToken ct);
}

public interface ICatalogRepository
{
    Task<Product?> FindProductByModelAsync(string modelCode, CancellationToken ct);

    Task<Product?> GetProductAsync(Guid productId, CancellationToken ct);

    Task<ProductSerial?> FindSerialAsync(string serialNumber, CancellationToken ct);
}

public interface IPolicyRepository
{
    Task<IReadOnlyList<PolicyVersion>> GetVersionsAsync(CancellationToken ct);

    Task<PolicyVersion?> GetVersionAsync(Guid policyVersionId, CancellationToken ct);

    Task<IReadOnlyList<PolicyClause>> GetClausesAsync(Guid policyVersionId, CancellationToken ct);

    Task<WarrantyPolicy?> FindPolicyByCodeAsync(string code, CancellationToken ct);

    Task<WarrantyPolicy?> GetPolicyAsync(Guid policyId, CancellationToken ct);

    void AddPolicy(WarrantyPolicy policy);

    void AddVersion(PolicyVersion version);

    void AddClause(PolicyClause clause);
}

public interface IClaimRepository
{
    void Add(Claim claim);

    Task<Claim?> GetAsync(Guid claimId, CancellationToken ct);

    Task<Claim?> FindByReferenceAsync(string reference, CancellationToken ct);

    void AddEvidence(ClaimEvidence evidence);

    Task<IReadOnlyList<ClaimEvidence>> GetEvidenceAsync(Guid claimId, CancellationToken ct);

    Task<ClaimEvidence?> GetEvidenceItemAsync(Guid claimId, Guid evidenceId, CancellationToken ct);

    /// <summary>Same-tenant history counts for the serial: open-or-90-day duplicates, accidental approvals, hash reuse.</summary>
    Task<ClaimHistoryCounts> GetHistoryCountsAsync(
        Guid claimId, string serialNumber, DateOnly claimDate, IReadOnlyCollection<string> evidenceHashes, CancellationToken ct);

    /// <summary>
    /// All claims of the current tenant with the serial number (compared in storage form), including
    /// the asking claim; the duplicate rule itself is applied by the caller (research R25).
    /// </summary>
    Task<IReadOnlyList<Claim>> ListForSerialAsync(string serialNumber, CancellationToken ct);

    /// <summary>Number of the customer's claims (current tenant) created before <paramref name="createdBefore"/>.</summary>
    Task<int> CountCustomerClaimsAsync(Guid customerId, DateTimeOffset createdBefore, CancellationToken ct);

    Task<ClaimPage> ListAsync(ClaimStatus? status, int page, int pageSize, CancellationToken ct);

    /// <summary>The claim's current row version (PostgreSQL <c>xmin</c>), used as its ETag; null when the claim is not visible.</summary>
    Task<uint?> GetRowVersionAsync(Guid claimId, CancellationToken ct);
}

public sealed record ClaimPage(IReadOnlyList<Claim> Items, int Page, int PageSize, int Total);

public interface IAdjudicationRepository
{
    void AddRun(AdjudicationRun run);

    Task<AdjudicationRun?> GetRunAsync(Guid claimId, int round, CancellationToken ct);

    Task<AdjudicationRun?> GetLatestRunAsync(Guid claimId, CancellationToken ct);

    void AddIntakeResult(IntakeResult result);

    void AddEvidenceFinding(EvidenceFinding finding);

    void AddRetrievedPolicyRef(RetrievedPolicyRef reference);

    void AddPolicyAssessment(PolicyAssessment assessment);

    void AddRiskAssessment(RiskAssessment assessment);

    void AddRecommendation(Recommendation recommendation);

    void AddGuardrailEvaluation(GuardrailEvaluation evaluation);

    Task<RunRecord?> GetRunRecordAsync(Guid runId, CancellationToken ct);

    /// <summary>The AI decision and disposition of the latest run of each given claim that has a run (claim lists).</summary>
    Task<IReadOnlyDictionary<Guid, LatestRunOutcome>> GetLatestOutcomesAsync(IReadOnlyCollection<Guid> claimIds, CancellationToken ct);
}

/// <summary>The outcome of a claim's latest run: the recommendation's decision and the guardrail disposition, when recorded.</summary>
public sealed record LatestRunOutcome(Guid ClaimId, Guid RunId, AiDecision? AiDecision, Disposition? Disposition);

/// <summary>Everything persisted for one run, as read back for claim detail, review and trace views.</summary>
public sealed record RunRecord(
    AdjudicationRun Run,
    IntakeResult? Intake,
    IReadOnlyList<EvidenceFinding> EvidenceFindings,
    IReadOnlyList<RetrievedPolicyRef> PolicyReferences,
    PolicyAssessment? PolicyAssessment,
    RiskAssessment? Risk,
    Recommendation? Recommendation,
    GuardrailEvaluation? Guardrails);

public interface IReviewRepository
{
    void Add(ReviewDecision decision);

    Task<IReadOnlyList<ReviewDecision>> GetForClaimAsync(Guid claimId, CancellationToken ct);
}

public interface IAiOpsRepository
{
    void AddModelCall(ModelCall call);

    void AddToolCall(ToolCall call);

    void AddRagQuery(RagQuery query);

    Task<AiOpsRecords> GetForRunAsync(Guid runId, CancellationToken ct);
}

/// <summary>Customer master data of the current tenant, stored for the simulated CRM.</summary>
public interface ICustomerRepository
{
    /// <summary>Finds by normalized email, including customers added but not yet saved in this unit of work.</summary>
    Task<Customer?> FindByEmailAsync(string normalizedEmail, CancellationToken ct);

    Task<Customer?> GetAsync(Guid customerId, CancellationToken ct);

    void Add(Customer customer);
}

/// <summary>Rows of the simulated service network and outboxes (<c>integration.*</c>) of the current tenant.</summary>
public interface IIntegrationRepository
{
    Task<IReadOnlyList<ServiceCenter>> GetServiceCentersAsync(Region region, CancellationToken ct);

    void AddRepairRequest(RepairRequest request);

    void AddNotification(Notification notification);
}

public sealed record AiOpsRecords(IReadOnlyList<ModelCall> ModelCalls, IReadOnlyList<ToolCall> ToolCalls, IReadOnlyList<RagQuery> RagQueries);
