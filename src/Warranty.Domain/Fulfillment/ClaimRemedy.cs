using Warranty.Domain.Claims;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The current remedy of an approved claim (<c>claims.claim_remedies</c>, one per claim). State changes
/// go only through the methods below, which enforce the remedy state machine (data-model.md
/// "Remedy"): a remedy is set by policy when exactly one is allowed, otherwise it waits for a
/// reviewer; a remedy change always passes <see cref="RemedyState.AwaitingDecision"/>;
/// <see cref="RemedyState.Completed"/> is final. A remedy outside <see cref="AllowedRemedies"/> is
/// always refused (FR-002). The remedy never changes the claim's final outcome (FR-016).
/// </summary>
public sealed class ClaimRemedy
{
    private IReadOnlyList<RemedyType> _allowedRemedies = [];

    private ClaimRemedy()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    /// <summary>The claim's applicable policy version used by the latest evaluation.</summary>
    public Guid PolicyVersionId { get; private set; }

    /// <summary>Scenario of the latest evaluation.</summary>
    public RemedyScenario Scenario { get; private set; }

    public RemedyState State { get; private set; }

    /// <summary>Null only while <see cref="RemedyState.AwaitingDecision"/>.</summary>
    public RemedyType? Remedy { get; private set; }

    /// <summary>From the latest evaluation; a decision must pick one of these.</summary>
    public IReadOnlyList<RemedyType> AllowedRemedies => _allowedRemedies;

    /// <summary>Set only while <see cref="RemedyState.AwaitingDecision"/>.</summary>
    public RemedyPendingReason? PendingReason { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Exactly one remedy is allowed: the policy sets it and the remedy is in progress (FR-011).</summary>
    public static ClaimRemedy SetByPolicy(
        Guid id,
        Guid tenantId,
        Guid claimId,
        Guid policyVersionId,
        RemedyScenario scenario,
        RemedyType remedy,
        IEnumerable<RemedyType> allowedRemedies,
        DateTimeOffset at)
    {
        var allowed = Distinct(allowedRemedies);
        if (allowed.Length != 1 || allowed[0] != remedy)
        {
            throw new InvalidClaimTransitionException(
                $"The policy can set a remedy only when it is the single allowed remedy; {remedy} is not.");
        }

        var claimRemedy = New(id, tenantId, claimId, policyVersionId, scenario, allowed, at);
        claimRemedy.State = RemedyState.InProgress;
        claimRemedy.Remedy = remedy;
        return claimRemedy;
    }

    /// <summary>The evaluation gives a choice, no rule, or lacks information: a reviewer decides.</summary>
    public static ClaimRemedy AwaitDecision(
        Guid id,
        Guid tenantId,
        Guid claimId,
        Guid policyVersionId,
        RemedyScenario scenario,
        IEnumerable<RemedyType> allowedRemedies,
        RemedyPendingReason pendingReason,
        DateTimeOffset at)
    {
        var claimRemedy = New(id, tenantId, claimId, policyVersionId, scenario, Distinct(allowedRemedies), at);
        claimRemedy.State = RemedyState.AwaitingDecision;
        claimRemedy.PendingReason = pendingReason;
        return claimRemedy;
    }

    /// <summary>
    /// A re-evaluation while in progress (repair failed at the limit, repair not economical) sends the
    /// remedy back to a reviewer with the new scenario and allowed remedies.
    /// </summary>
    public void ReturnToDecision(
        Guid policyVersionId,
        RemedyScenario scenario,
        IEnumerable<RemedyType> allowedRemedies,
        RemedyPendingReason pendingReason,
        DateTimeOffset at)
    {
        Require(RemedyState.InProgress, "return to decision");
        RequireId(policyVersionId, nameof(policyVersionId));

        PolicyVersionId = policyVersionId;
        Scenario = scenario;
        _allowedRemedies = Distinct(allowedRemedies);
        State = RemedyState.AwaitingDecision;
        Remedy = null;
        PendingReason = pendingReason;
        UpdatedAt = at;
    }

    /// <summary>A reviewer picks one of the allowed remedies; the remedy is then in progress (FR-015).</summary>
    public void Decide(RemedyType remedy, DateTimeOffset at)
    {
        Require(RemedyState.AwaitingDecision, "decide");
        RequireAllowed(remedy);

        State = RemedyState.InProgress;
        Remedy = remedy;
        PendingReason = null;
        UpdatedAt = at;
    }

    /// <summary>
    /// The remedy is fulfilled: a Repair was collected, or a Replacement/Refund completion was
    /// recorded. <paramref name="remedy"/> must be the current remedy. Final.
    /// </summary>
    public void Complete(RemedyType remedy, DateTimeOffset at)
    {
        Require(RemedyState.InProgress, "complete");
        RequireAllowed(remedy);
        if (Remedy != remedy)
        {
            throw new InvalidClaimTransitionException($"Cannot complete {remedy}; the current remedy is {Remedy}.");
        }

        State = RemedyState.Completed;
        UpdatedAt = at;
    }

    private static ClaimRemedy New(
        Guid id,
        Guid tenantId,
        Guid claimId,
        Guid policyVersionId,
        RemedyScenario scenario,
        IReadOnlyList<RemedyType> allowed,
        DateTimeOffset at)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("Remedy, tenant and claim IDs are required.");
        }

        RequireId(policyVersionId, nameof(policyVersionId));

        return new ClaimRemedy
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            PolicyVersionId = policyVersionId,
            Scenario = scenario,
            _allowedRemedies = allowed,
            UpdatedAt = at,
        };
    }

    private static RemedyType[] Distinct(IEnumerable<RemedyType> remedies)
    {
        ArgumentNullException.ThrowIfNull(remedies);
        return [.. remedies.Distinct()];
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A policy version ID is required.", name);
        }
    }

    private void Require(RemedyState expected, string action)
    {
        if (State != expected)
        {
            throw new InvalidClaimTransitionException($"Cannot {action} a remedy in state {State}.");
        }
    }

    private void RequireAllowed(RemedyType remedy)
    {
        if (!_allowedRemedies.Contains(remedy))
        {
            throw new InvalidClaimTransitionException(
                $"{remedy} is not an allowed remedy ({string.Join(", ", _allowedRemedies)}).");
        }
    }
}
