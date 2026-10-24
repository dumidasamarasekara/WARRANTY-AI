using System.Text.Json;
using Warranty.Domain.Policies;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The replacement of an approved claim whose remedy is Replacement (<c>claims.replacements</c>), one per
/// claim. Carries the policy version's replacement rules copied at creation, and owns its locations, the
/// faulty-product return, model proposals, reserved units and append-only events; status changes follow
/// the replacement state machine in data-model.md and go only through this aggregate (transitions are
/// added in T325, T336, T343 and T355).
/// </summary>
public sealed class Replacement
{
    private readonly List<ReplacementLocation> _locations = [];
    private readonly List<ReplacementModelProposal> _proposals = [];
    private readonly List<ReplacementUnit> _units = [];
    private readonly List<ReplacementEvent> _events = [];

    private Replacement()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    /// <summary>The policy version whose <c>replacement</c> rules were copied.</summary>
    public Guid PolicyVersionId { get; private set; }

    /// <summary>Copied from the rules at creation.</summary>
    public ReplacementProductRule ProductRule { get; private set; }

    /// <summary>Copied from the rules at creation.</summary>
    public ReplacementReturnRule ReturnRule { get; private set; }

    /// <summary>Copied from the rules at creation; minimum cover of the handed-out unit (0 = remainder of the original warranty).</summary>
    public int MinimumCoverMonths { get; private set; }

    public ReplacementStatus Status { get; private set; }

    public ReturnState ReturnState { get; private set; }

    /// <summary>Null while no location is assigned (no eligible location was found).</summary>
    public Guid? ActiveLocationId { get; private set; }

    /// <summary>The latest reserved unit (it may have ended); null until the first reservation.</summary>
    public Guid? CurrentUnitId { get; private set; }

    /// <summary>The proposal awaiting a reviewer; null when none is pending.</summary>
    public Guid? PendingProposalId { get; private set; }

    /// <summary>The equivalent model approved by a reviewer; null = the claimed model only.</summary>
    public Guid? ApprovedProductId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ReplacementLocation> Locations => _locations;

    /// <summary>The faulty-product return; null until it is recorded.</summary>
    public FaultyProductReturn? Return { get; private set; }

    public IReadOnlyList<ReplacementModelProposal> Proposals => _proposals;

    public IReadOnlyList<ReplacementUnit> Units => _units;

    /// <summary>The events of this replacement in <see cref="ReplacementEvent.Seq"/> order as loaded or appended.</summary>
    public IReadOnlyList<ReplacementEvent> Events => _events;

    public ReplacementLocation? ActiveLocation =>
        ActiveLocationId is { } id ? _locations.SingleOrDefault(l => l.Id == id) : null;

    public ReplacementUnit? CurrentUnit =>
        CurrentUnitId is { } id ? _units.SingleOrDefault(u => u.Id == id) : null;

    public ReplacementModelProposal? PendingProposal =>
        PendingProposalId is { } id ? _proposals.SingleOrDefault(p => p.Id == id) : null;

    /// <summary>
    /// Creates the replacement in <paramref name="initialStatus"/> (<see cref="ReplacementStatus.AwaitingReturn"/>
    /// only under <see cref="ReplacementReturnRule.BeforeReservation"/>, research R9; otherwise
    /// <see cref="ReplacementStatus.AwaitingUnit"/>) with the copied rules and <see cref="ReturnState.NotYetReturned"/>.
    /// When a location is suggested it becomes the active location (<see cref="LocationAssignment.Suggested"/>,
    /// recorded by the system) and a <see cref="ReplacementEventType.LocationSuggested"/> event is appended;
    /// otherwise no location is assigned.
    /// </summary>
    public static Replacement Create(
        Guid id,
        Guid tenantId,
        Guid claimId,
        Guid policyVersionId,
        ReplacementProductRule productRule,
        ReplacementReturnRule returnRule,
        int minimumCoverMonths,
        ReplacementStatus initialStatus,
        Guid? suggestedServiceCenterId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || claimId == Guid.Empty || policyVersionId == Guid.Empty)
        {
            throw new ArgumentException("Replacement, tenant, claim and policy-version IDs are required.");
        }

        if (!Enum.IsDefined(productRule) || !Enum.IsDefined(returnRule))
        {
            throw new ArgumentException("The replacement product and return rules must be known values.");
        }

        if (minimumCoverMonths is < ReplacementRules.MinCoverMonths or > ReplacementRules.MaxCoverMonths)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumCoverMonths),
                $"The minimum replacement cover must be {ReplacementRules.MinCoverMonths}–{ReplacementRules.MaxCoverMonths} months.");
        }

        if (initialStatus is not (ReplacementStatus.AwaitingReturn or ReplacementStatus.AwaitingUnit))
        {
            throw new ArgumentException("A replacement starts awaiting the return or awaiting a unit.", nameof(initialStatus));
        }

        if (initialStatus == ReplacementStatus.AwaitingReturn && returnRule != ReplacementReturnRule.BeforeReservation)
        {
            throw new ArgumentException(
                "A replacement awaits the return first only when the product is returned before reservation.", nameof(initialStatus));
        }

        if (suggestedServiceCenterId == Guid.Empty)
        {
            throw new ArgumentException("A suggested location ID must not be empty.", nameof(suggestedServiceCenterId));
        }

        var replacement = new Replacement
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            PolicyVersionId = policyVersionId,
            ProductRule = productRule,
            ReturnRule = returnRule,
            MinimumCoverMonths = minimumCoverMonths,
            Status = initialStatus,
            ReturnState = ReturnState.NotYetReturned,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

        if (suggestedServiceCenterId is { } centreId)
        {
            var location = replacement.AddLocation(
                centreId, LocationAssignment.Suggested, reason: null, ReplacementLocation.SystemRecorder, createdAt);
            replacement.AppendEvent(
                ReplacementEventType.LocationSuggested,
                fromStatus: null,
                toStatus: initialStatus,
                reportedAt: createdAt,
                recordedAt: createdAt,
                ReportSource.System,
                ReplacementEvent.SystemActor,
                note: null,
                payloadJson: JsonSerializer.Serialize(new { serviceCenterId = centreId, locationId = location.Id }));
        }

        return replacement;
    }

    /// <summary>The next gap-free event sequence number.</summary>
    internal int NextEventSeq => _events.Count == 0 ? 1 : _events.Max(e => e.Seq) + 1;

    /// <summary>Deactivates the active location (if any) and makes a new one active.</summary>
    internal ReplacementLocation AddLocation(
        Guid serviceCenterId, LocationAssignment assignment, string? reason, string recordedBy, DateTimeOffset recordedAt)
    {
        var location = ReplacementLocation.Create(TenantId, Id, serviceCenterId, assignment, reason, recordedBy, recordedAt);
        ActiveLocation?.Deactivate();
        _locations.Add(location);
        ActiveLocationId = location.Id;
        UpdatedAt = recordedAt;
        return location;
    }

    /// <summary>Records the (single) faulty-product return; corrections go through events.</summary>
    internal FaultyProductReturn AddReturn(
        DateOnly returnedOn,
        Guid? serviceCenterId,
        string? serialSeen,
        bool serialMatches,
        Guid? repairHandoverId,
        string? note,
        string recordedBy,
        DateTimeOffset recordedAt)
    {
        if (Return is not null)
        {
            throw new InvalidOperationException("The faulty-product return has already been recorded.");
        }

        Return = FaultyProductReturn.Create(
            TenantId, Id, returnedOn, serviceCenterId, serialSeen, serialMatches, repairHandoverId, note, recordedBy, recordedAt);
        UpdatedAt = recordedAt;
        return Return;
    }

    /// <summary>Adds a pending model proposal; at most one may be pending.</summary>
    internal ReplacementModelProposal AddProposal(Guid productId, string reason, string proposedBy, DateTimeOffset proposedAt)
    {
        if (PendingProposal is not null)
        {
            throw new InvalidOperationException("A model proposal is already pending.");
        }

        var proposal = ReplacementModelProposal.Create(TenantId, Id, productId, reason, proposedBy, proposedAt);
        _proposals.Add(proposal);
        PendingProposalId = proposal.Id;
        UpdatedAt = proposedAt;
        return proposal;
    }

    /// <summary>Decides the pending proposal; an approval makes its model the approved product.</summary>
    internal ReplacementModelProposal DecidePendingProposal(
        ModelProposalState decision, string decidedBy, string? decisionReason, DateTimeOffset decidedAt)
    {
        var proposal = PendingProposal ?? throw new InvalidOperationException("No model proposal is pending.");
        proposal.Decide(decision, decidedBy, decisionReason, decidedAt);
        PendingProposalId = null;
        if (decision == ModelProposalState.Approved)
        {
            ApprovedProductId = proposal.ProductId;
        }

        UpdatedAt = decidedAt;
        return proposal;
    }

    /// <summary>Reserves the next unit at <paramref name="serviceCenterId"/> and makes it current.</summary>
    internal ReplacementUnit AddUnit(Guid serviceCenterId, Guid productId, string serialNumber, DateTimeOffset reservedAt)
    {
        if (CurrentUnit is { IsReserved: true })
        {
            throw new InvalidOperationException("The current replacement unit is still reserved.");
        }

        var seq = _units.Count == 0 ? 1 : _units.Max(u => u.Seq) + 1;
        var unit = ReplacementUnit.Reserve(TenantId, Id, seq, serviceCenterId, productId, serialNumber, reservedAt);
        _units.Add(unit);
        CurrentUnitId = unit.Id;
        UpdatedAt = reservedAt;
        return unit;
    }

    /// <summary>Moves the replacement to <paramref name="status"/> (the caller checks the transition).</summary>
    internal void SetStatus(ReplacementStatus status, DateTimeOffset at)
    {
        Status = status;
        UpdatedAt = at;
    }

    /// <summary>Sets where the faulty product stands (the caller checks the transition).</summary>
    internal void SetReturnState(ReturnState returnState, DateTimeOffset at)
    {
        ReturnState = returnState;
        UpdatedAt = at;
    }

    /// <summary>Appends an event with the next gap-free sequence number.</summary>
    internal ReplacementEvent AppendEvent(
        ReplacementEventType type,
        ReplacementStatus? fromStatus,
        ReplacementStatus? toStatus,
        DateTimeOffset reportedAt,
        DateTimeOffset recordedAt,
        ReportSource source,
        string actor,
        string? note,
        string? payloadJson,
        long? correctsEventId = null)
    {
        var replacementEvent = ReplacementEvent.Create(
            TenantId, Id, ClaimId, NextEventSeq, type, fromStatus, toStatus, reportedAt, recordedAt, source, actor, note,
            payloadJson, correctsEventId);
        _events.Add(replacementEvent);
        UpdatedAt = recordedAt;
        return replacementEvent;
    }
}
