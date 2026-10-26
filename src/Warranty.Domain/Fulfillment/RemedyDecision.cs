using Warranty.Domain.Claims;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// One row of a claim's remedy history (<c>claims.remedy_decisions</c>): an evaluator output
/// (<see cref="RemedyDecisionKind.Evaluated"/>) or a remedy being set
/// (<see cref="RemedyDecisionKind.Decided"/>). Rows are insert-only: the type has no mutators and
/// the repository has no update method (data-model.md).
/// </summary>
public sealed class RemedyDecision
{
    /// <summary>The actor recorded for evaluations and policy decisions.</summary>
    public const string SystemActor = "system";

    public const int MinReasonLength = 10;

    public const int MaxReasonLength = 1000;

    /// <summary>Remedy-rules table rows an evaluation can come from (003 adds <c>replacementNotAvailable</c>).</summary>
    public static readonly IReadOnlyList<string> TableRows =
        ["coveredDefect", "repairNotEconomical", "repairFailed", "protectionPlan", "replacementNotAvailable", "none"];

    private IReadOnlyList<RemedyType> _allowedRemedies = [];

    private RemedyDecision()
    {
        TableRow = Actor = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    /// <summary>Per claim, from 1.</summary>
    public int Seq { get; private set; }

    public RemedyDecisionKind Kind { get; private set; }

    public RemedyScenario Scenario { get; private set; }

    public Guid PolicyVersionId { get; private set; }

    /// <summary>One of <see cref="TableRows"/>.</summary>
    public string TableRow { get; private set; }

    public IReadOnlyList<RemedyType> AllowedRemedies => _allowedRemedies;

    /// <summary>Set for <see cref="RemedyDecisionKind.Decided"/>.</summary>
    public RemedyType? Remedy { get; private set; }

    /// <summary>Set for <see cref="RemedyDecisionKind.Decided"/>.</summary>
    public RemedyDecidedBy? DecidedBy { get; private set; }

    /// <summary><see cref="SystemActor"/> or the reviewer's <c>sub</c>.</summary>
    public string Actor { get; private set; }

    /// <summary>Reviewer's reason; the executor requires it for every change after the first remedy (FR-015).</summary>
    public string? Reason { get; private set; }

    /// <summary>Failed repair attempts at evaluation time.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>Estimate used by <see cref="RemedyScenario.RepairNotEconomical"/>.</summary>
    public decimal? EstimateAmount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Records an evaluator output (always by the system).</summary>
    public static RemedyDecision Evaluated(
        Guid id,
        Guid tenantId,
        Guid claimId,
        int seq,
        RemedyScenario scenario,
        Guid policyVersionId,
        string tableRow,
        IEnumerable<RemedyType> allowedRemedies,
        int failedAttempts,
        decimal? estimateAmount,
        DateTimeOffset createdAt)
        => New(id, tenantId, claimId, seq, RemedyDecisionKind.Evaluated, scenario, policyVersionId, tableRow,
            allowedRemedies, failedAttempts, estimateAmount, createdAt);

    /// <summary>
    /// Records a remedy being set, by the policy (actor <see cref="SystemActor"/>, no reason) or by a
    /// reviewer (actor = reviewer <c>sub</c>, optional reason of 10–1000 characters). The remedy must
    /// be one of <paramref name="allowedRemedies"/>.
    /// </summary>
    public static RemedyDecision Decided(
        Guid id,
        Guid tenantId,
        Guid claimId,
        int seq,
        RemedyScenario scenario,
        Guid policyVersionId,
        string tableRow,
        IEnumerable<RemedyType> allowedRemedies,
        RemedyType remedy,
        RemedyDecidedBy decidedBy,
        string actor,
        string? reason,
        int failedAttempts,
        decimal? estimateAmount,
        DateTimeOffset createdAt)
    {
        var row = New(id, tenantId, claimId, seq, RemedyDecisionKind.Decided, scenario, policyVersionId, tableRow,
            allowedRemedies, failedAttempts, estimateAmount, createdAt);

        if (!row._allowedRemedies.Contains(remedy))
        {
            throw new InvalidClaimTransitionException(
                $"{remedy} is not an allowed remedy ({string.Join(", ", row._allowedRemedies)}).");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (decidedBy == RemedyDecidedBy.Policy)
        {
            if (actor != SystemActor || trimmedReason is not null)
            {
                throw new ArgumentException("A policy decision is recorded by the system without a reason.", nameof(actor));
            }
        }
        else if (actor == SystemActor)
        {
            throw new ArgumentException("A reviewer decision is recorded with the reviewer's sub.", nameof(actor));
        }

        if (trimmedReason is not null && trimmedReason.Length is < MinReasonLength or > MaxReasonLength)
        {
            throw new ArgumentException($"A reason must be {MinReasonLength}–{MaxReasonLength} characters.", nameof(reason));
        }

        row.Remedy = remedy;
        row.DecidedBy = decidedBy;
        row.Actor = actor;
        row.Reason = trimmedReason;
        return row;
    }

    private static RemedyDecision New(
        Guid id,
        Guid tenantId,
        Guid claimId,
        int seq,
        RemedyDecisionKind kind,
        RemedyScenario scenario,
        Guid policyVersionId,
        string tableRow,
        IEnumerable<RemedyType> allowedRemedies,
        int failedAttempts,
        decimal? estimateAmount,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || claimId == Guid.Empty || policyVersionId == Guid.Empty)
        {
            throw new ArgumentException("Decision, tenant, claim and policy version IDs are required.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(seq, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(failedAttempts);
        if (estimateAmount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimateAmount), "An estimate cannot be negative.");
        }

        if (!TableRows.Contains(tableRow))
        {
            throw new ArgumentException($"'{tableRow}' is not a remedy-rules table row.", nameof(tableRow));
        }

        ArgumentNullException.ThrowIfNull(allowedRemedies);

        return new RemedyDecision
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            Seq = seq,
            Kind = kind,
            Scenario = scenario,
            PolicyVersionId = policyVersionId,
            TableRow = tableRow,
            _allowedRemedies = [.. allowedRemedies.Distinct()],
            Actor = SystemActor,
            FailedAttempts = failedAttempts,
            EstimateAmount = estimateAmount,
            CreatedAt = createdAt,
        };
    }
}
