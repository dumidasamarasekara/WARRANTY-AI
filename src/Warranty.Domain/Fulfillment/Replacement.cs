using System.Text.Json;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The replacement of an approved claim whose remedy is Replacement (<c>claims.replacements</c>), one per
/// claim. Carries the policy version's replacement rules copied at creation, and owns its locations, the
/// faulty-product return, model proposals, reserved units and append-only events; status changes follow
/// the replacement state machine in data-model.md and go only through this aggregate (transitions are
/// added in T325, T336, T337, T343, T345 and T355).
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

    // Transitions (data-model.md, Replacement state machine). Role rules are checked first: an auditor records no
    // event and a claims agent no reviewer-only event (ReturnAccepted, ModelApproved, ModelDeclined) — refused with an
    // UnauthorizedAccessException (403). A transition not allowed from the current status throws an
    // InvalidClaimTransitionException naming the current status and the allowed events (409). A refused event leaves
    // the replacement unchanged; an allowed one appends exactly one ReplacementEvent and returns it.

    /// <summary>
    /// The staff event types allowed from the current status for <paramref name="role"/> (drives
    /// <c>ReplacementView.allowedEvents</c> and the 409 detail): the rows of the data-model.md table, where
    /// <see cref="ReplacementEventType.LocationChanged"/> needs <see cref="ReplacementStatus.AwaitingReturn"/> or
    /// <see cref="ReplacementStatus.AwaitingUnit"/>; <see cref="ReplacementEventType.ReturnRecorded"/>,
    /// <see cref="ReplacementEventType.UnitReserved"/> and <see cref="ReplacementEventType.Moved"/> an active location;
    /// <see cref="ReplacementEventType.ReturnRecorded"/> no recorded return, and outside <see cref="ReplacementStatus.AwaitingReturn"/>
    /// the <see cref="ReplacementReturnRule.AtCollection"/> rule; <see cref="ReplacementEventType.ReturnAccepted"/> a
    /// <see cref="ReturnState.Mismatch"/>; <see cref="ReplacementEventType.ModelProposed"/> the
    /// <see cref="ReplacementProductRule.SameCategoryAtLeastClaimValue"/> rule and no pending proposal;
    /// <see cref="ReplacementEventType.ModelApproved"/> and <see cref="ReplacementEventType.ModelDeclined"/> a pending proposal;
    /// <see cref="ReplacementEventType.UnitReserved"/> and <see cref="ReplacementEventType.HandedToCustomer"/> no unresolved
    /// mismatch; a correction is allowed in every status except <see cref="ReplacementStatus.HandedToCustomer"/> and
    /// <see cref="ReplacementStatus.Cancelled"/>. Reviewer-only events are left out for a claims agent, every event for an
    /// auditor; <see cref="ReplacementEventType.Cancelled"/> (system only) is never listed.
    /// </summary>
    public IReadOnlyList<ReplacementEventType> AllowedEvents(StaffRole role)
    {
        if (role is not (StaffRole.ClaimsAgent or StaffRole.ClaimsReviewer))
        {
            return [];
        }

        var open = Status is ReplacementStatus.AwaitingReturn or ReplacementStatus.AwaitingUnit or ReplacementStatus.ReadyForCollection;
        var hasLocation = ActiveLocationId is not null;
        var mismatch = ReturnState == ReturnState.Mismatch;
        var events = new List<ReplacementEventType>();

        if (Status is ReplacementStatus.AwaitingReturn or ReplacementStatus.AwaitingUnit)
        {
            events.Add(ReplacementEventType.LocationChanged);
        }

        if (hasLocation && Return is null && ReturnState == ReturnState.NotYetReturned
            && (Status == ReplacementStatus.AwaitingReturn
                || (Status is ReplacementStatus.AwaitingUnit or ReplacementStatus.ReadyForCollection
                    && ReturnRule == ReplacementReturnRule.AtCollection)))
        {
            events.Add(ReplacementEventType.ReturnRecorded);
        }

        if (open && mismatch)
        {
            events.Add(ReplacementEventType.ReturnAccepted);
        }

        if (Status == ReplacementStatus.AwaitingUnit)
        {
            if (ProductRule == ReplacementProductRule.SameCategoryAtLeastClaimValue && PendingProposalId is null)
            {
                events.Add(ReplacementEventType.ModelProposed);
            }

            if (PendingProposalId is not null)
            {
                events.Add(ReplacementEventType.ModelApproved);
                events.Add(ReplacementEventType.ModelDeclined);
            }

            if (hasLocation && !mismatch)
            {
                events.Add(ReplacementEventType.UnitReserved);
            }
        }

        if (Status == ReplacementStatus.ReadyForCollection)
        {
            events.Add(ReplacementEventType.UnitRefused);
        }

        if (open && hasLocation)
        {
            events.Add(ReplacementEventType.Moved);
        }

        if (Status == ReplacementStatus.ReadyForCollection && !mismatch)
        {
            events.Add(ReplacementEventType.HandedToCustomer);
        }

        if (Status == ReplacementStatus.AwaitingUnit)
        {
            events.Add(ReplacementEventType.NotAvailable);
        }

        if (Status is not (ReplacementStatus.HandedToCustomer or ReplacementStatus.Cancelled))
        {
            events.Add(ReplacementEventType.Correction);
        }

        return role == StaffRole.ClaimsReviewer ? events : [.. events.Where(e => !IsReviewerOnly(e))];
    }

    /// <summary>
    /// <see cref="ReplacementEventType.LocationChanged"/>: <see cref="ReplacementStatus.AwaitingReturn"/> or
    /// <see cref="ReplacementStatus.AwaitingUnit"/> stays; the staff-chosen location becomes the only active location
    /// (<see cref="LocationAssignment.ChosenByStaff"/>, reason required). Whether the location is an eligible replacement
    /// location of the tenant is checked by the use case.
    /// </summary>
    public ReplacementEvent ChangeLocation(Guid serviceCenterId, string reason, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (serviceCenterId == Guid.Empty)
        {
            throw new ArgumentException("A replacement location ID is required.", nameof(serviceCenterId));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to change the replacement location.", nameof(reason));
        }

        Require(ReplacementEventType.LocationChanged, report);

        var previous = ActiveLocation?.ServiceCenterId;
        var location = AddLocation(serviceCenterId, LocationAssignment.ChosenByStaff, reason, report.Actor, report.RecordedAt);
        return Append(
            ReplacementEventType.LocationChanged,
            Status,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["serviceCenterId"] = serviceCenterId,
                ["previousServiceCenterId"] = previous,
                ["locationId"] = location.Id,
                ["reason"] = location.Reason,
            });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.ReturnRecorded"/>: the faulty product was returned to the active location on
    /// <paramref name="returnedOn"/> with <paramref name="serialSeen"/>; <paramref name="serialMatches"/> is the use case's
    /// <c>EvidenceMatchRules</c> comparison with the claim's serial (001 FR-016, FR-019). From
    /// <see cref="ReplacementStatus.AwaitingReturn"/> (<see cref="ReplacementReturnRule.BeforeReservation"/>) a match moves to
    /// <see cref="ReplacementStatus.AwaitingUnit"/>; from <see cref="ReplacementStatus.AwaitingUnit"/> or
    /// <see cref="ReplacementStatus.ReadyForCollection"/> (<see cref="ReplacementReturnRule.AtCollection"/> only) the status stays.
    /// A match sets <see cref="ReturnState.Returned"/>; a mismatch sets <see cref="ReturnState.Mismatch"/> and keeps the status.
    /// The return is recorded once (corrections go through events).
    /// </summary>
    public ReplacementEvent RecordReturn(DateOnly returnedOn, string serialSeen, bool serialMatches, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(serialSeen))
        {
            throw new ArgumentException("The serial seen on the returned product is required.", nameof(serialSeen));
        }

        Require(ReplacementEventType.ReturnRecorded, report);

        var from = Status;
        var location = ActiveLocation!;
        var faultyReturn = AddReturn(
            returnedOn, location.ServiceCenterId, serialSeen, serialMatches, repairHandoverId: null, report.Note, report.Actor,
            report.RecordedAt);
        SetReturnState(serialMatches ? ReturnState.Returned : ReturnState.Mismatch, report.RecordedAt);
        if (serialMatches && from == ReplacementStatus.AwaitingReturn)
        {
            SetStatus(ReplacementStatus.AwaitingUnit, report.RecordedAt);
        }

        return Append(
            ReplacementEventType.ReturnRecorded,
            from,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["returnId"] = faultyReturn.Id,
                ["serviceCenterId"] = location.ServiceCenterId,
                ["returnedOn"] = returnedOn,
                ["serialSeen"] = faultyReturn.SerialSeen,
                ["serialMatches"] = serialMatches,
                ["returnState"] = ReturnState.ToString(),
            });
    }

    /// <summary>
    /// The <see cref="ReplacementEventType.ReturnRecorded"/> event of the system (research R9, FR-020) when the replacement
    /// follows a 002 repair whose centre holds the faulty product (<c>Repair.HandoverHoldingProduct</c>): recorded right after
    /// <see cref="Create"/> in the same transaction, on the repair hand-over's date, with <see cref="ReportSource.System"/>
    /// referring to <paramref name="repairHandoverId"/>; no location or serial is recorded and the status stays.
    /// <see cref="ReturnState.HeldByRepairCentre"/> satisfies both return rules, so no staff return and no return confirmation
    /// at the hand-over are needed. The return is recorded once, while the replacement awaits a unit and has no return
    /// (an <see cref="InvalidClaimTransitionException"/> otherwise).
    /// </summary>
    public ReplacementEvent RecordHeldReturn(Guid repairHandoverId, DateOnly handedOverOn, DateTimeOffset recordedAt)
    {
        if (repairHandoverId == Guid.Empty)
        {
            throw new ArgumentException("The repair hand-over ID is required.", nameof(repairHandoverId));
        }

        if (Status != ReplacementStatus.AwaitingUnit || Return is not null || ReturnState != ReturnState.NotYetReturned)
        {
            throw new InvalidClaimTransitionException(
                $"Cannot record the product held by a repair centre on a replacement in status {Status} (return {ReturnState}).");
        }

        var faultyReturn = AddReturn(
            handedOverOn, serviceCenterId: null, serialSeen: null, serialMatches: true, repairHandoverId, note: null,
            ReplacementEvent.SystemActor, recordedAt);
        SetReturnState(ReturnState.HeldByRepairCentre, recordedAt);
        return AppendEvent(
            ReplacementEventType.ReturnRecorded,
            Status,
            Status,
            reportedAt: recordedAt,
            recordedAt: recordedAt,
            ReportSource.System,
            ReplacementEvent.SystemActor,
            note: null,
            payloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["returnId"] = faultyReturn.Id,
                ["repairHandoverId"] = repairHandoverId,
                ["returnedOn"] = handedOverOn,
                ["returnState"] = ReturnState.ToString(),
            }));
    }

    /// <summary>
    /// <see cref="ReplacementEventType.ReturnAccepted"/> (claims reviewer only): a reviewer accepts a returned product whose
    /// serial did not match (<see cref="ReturnState.Mismatch"/> → <see cref="ReturnState.Accepted"/>, reason required);
    /// <see cref="ReplacementStatus.AwaitingReturn"/> → <see cref="ReplacementStatus.AwaitingUnit"/>, any other status stays.
    /// The return keeps its serial check (<see cref="FaultyProductReturn.SerialMatches"/> stays false); only its consequence is
    /// accepted. That the reviewer did not submit the claim is checked by the use case.
    /// </summary>
    public ReplacementEvent AcceptReturn(string reason, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to accept the returned product.", nameof(reason));
        }

        Require(ReplacementEventType.ReturnAccepted, report);

        var from = Status;
        SetReturnState(ReturnState.Accepted, report.RecordedAt);
        if (from == ReplacementStatus.AwaitingReturn)
        {
            SetStatus(ReplacementStatus.AwaitingUnit, report.RecordedAt);
        }

        return Append(
            ReplacementEventType.ReturnAccepted,
            from,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["returnId"] = Return?.Id,
                ["serialSeen"] = Return?.SerialSeen,
                ["reason"] = reason.Trim(),
                ["returnState"] = ReturnState.ToString(),
            });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.ModelProposed"/>: <see cref="ReplacementStatus.AwaitingUnit"/> stays; adds a pending
    /// proposal of <paramref name="productId"/> (reason required). Needs the
    /// <see cref="ReplacementProductRule.SameCategoryAtLeastClaimValue"/> rule and no pending proposal; the model check
    /// (<c>ReplacementPolicy.CheckModel</c>) is done by the use case.
    /// </summary>
    public ReplacementEvent ProposeModel(Guid productId, string reason, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (productId == Guid.Empty)
        {
            throw new ArgumentException("The proposed model is required.", nameof(productId));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to propose another model.", nameof(reason));
        }

        Require(ReplacementEventType.ModelProposed, report);

        var proposal = AddProposal(productId, reason.Trim(), report.Actor, report.RecordedAt);
        return Append(
            ReplacementEventType.ModelProposed,
            Status,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["proposalId"] = proposal.Id,
                ["productId"] = proposal.ProductId,
                ["reason"] = proposal.Reason,
            });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.ModelApproved"/> (claims reviewer only): <see cref="ReplacementStatus.AwaitingUnit"/> stays;
    /// the pending proposal is approved and its model becomes <see cref="ApprovedProductId"/>. That the reviewer did not submit
    /// the claim is checked by the use case.
    /// </summary>
    public ReplacementEvent ApproveModel(ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Require(ReplacementEventType.ModelApproved, report);

        var proposal = DecidePendingProposal(ModelProposalState.Approved, report.Actor, decisionReason: null, report.RecordedAt);
        return Append(
            ReplacementEventType.ModelApproved,
            Status,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["proposalId"] = proposal.Id,
                ["productId"] = proposal.ProductId,
            });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.ModelDeclined"/> (claims reviewer only): <see cref="ReplacementStatus.AwaitingUnit"/> stays;
    /// the pending proposal is declined (reason required) and the claimed model (or an earlier approved one) remains.
    /// </summary>
    public ReplacementEvent DeclineModel(string reason, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to decline the proposed model.", nameof(reason));
        }

        Require(ReplacementEventType.ModelDeclined, report);

        var proposal = DecidePendingProposal(ModelProposalState.Declined, report.Actor, reason, report.RecordedAt);
        return Append(
            ReplacementEventType.ModelDeclined,
            Status,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["proposalId"] = proposal.Id,
                ["productId"] = proposal.ProductId,
                ["reason"] = proposal.DecisionReason,
            });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.UnitReserved"/>: <see cref="ReplacementStatus.AwaitingUnit"/> →
    /// <see cref="ReplacementStatus.ReadyForCollection"/>; reserves the next <see cref="ReplacementUnit"/> of
    /// <paramref name="productId"/> with <paramref name="serialNumber"/> at the active location and makes it current. Needs an
    /// active location and no unresolved mismatch. The use case resolves the model (the claimed one or
    /// <see cref="ApprovedProductId"/>), checks the serial rules (FR-022) and queues the ready notification once per unit.
    /// </summary>
    public ReplacementEvent ReserveUnit(Guid productId, string serialNumber, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (productId == Guid.Empty)
        {
            throw new ArgumentException("The product of the reserved unit is required.", nameof(productId));
        }

        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new ArgumentException("The serial number of the reserved unit is required.", nameof(serialNumber));
        }

        Require(ReplacementEventType.UnitReserved, report);

        var location = ActiveLocation!;
        var unit = AddUnit(location.ServiceCenterId, productId, ProductSerial.NormalizeSerial(serialNumber), report.RecordedAt);
        SetStatus(ReplacementStatus.ReadyForCollection, report.RecordedAt);
        return Append(
            ReplacementEventType.UnitReserved,
            ReplacementStatus.AwaitingUnit,
            ReplacementStatus.ReadyForCollection,
            report,
            new Dictionary<string, object?>
            {
                ["unitId"] = unit.Id,
                ["unitSeq"] = unit.Seq,
                ["serviceCenterId"] = unit.ServiceCenterId,
                ["productId"] = unit.ProductId,
                ["serialNumber"] = unit.SerialNumber,
            });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.UnitRefused"/>: <see cref="ReplacementStatus.ReadyForCollection"/> →
    /// <see cref="ReplacementStatus.AwaitingUnit"/>; the current unit ends as <see cref="ReplacementUnitResult.Refused"/>
    /// (reason required). The remedy is unchanged (FR-027). Pending T355.
    /// </summary>
    public ReplacementEvent RefuseUnit(string reason, ReplacementReport report)
        => throw new NotImplementedException($"Pending T355 ({reason}, {report}).");

    /// <summary>
    /// <see cref="ReplacementEventType.Moved"/>: from <see cref="ReplacementStatus.AwaitingReturn"/> (stays),
    /// <see cref="ReplacementStatus.AwaitingUnit"/> (stays) or <see cref="ReplacementStatus.ReadyForCollection"/> (→
    /// <see cref="ReplacementStatus.AwaitingUnit"/>); another location becomes the only active one
    /// (<see cref="LocationAssignment.Moved"/>, reason required) and a reserved current unit ends as
    /// <see cref="ReplacementUnitResult.Released"/>. Needs an active location. Pending T355.
    /// </summary>
    public ReplacementEvent Move(Guid serviceCenterId, string reason, ReplacementReport report)
        => throw new NotImplementedException($"Pending T355 ({serviceCenterId}, {reason}, {report}).");

    /// <summary>
    /// <see cref="ReplacementEventType.HandedToCustomer"/>: <see cref="ReplacementStatus.ReadyForCollection"/> →
    /// <see cref="ReplacementStatus.HandedToCustomer"/> (final); the current unit ends as
    /// <see cref="ReplacementUnitResult.HandedOver"/> on <paramref name="handedOverOn"/>. Refused while the return state is
    /// <see cref="ReturnState.Mismatch"/>. Under <see cref="ReplacementReturnRule.AtCollection"/>,
    /// <paramref name="returnConfirmed"/> must be true unless the product was already returned or is held by a repair centre
    /// (an <see cref="ArgumentException"/> otherwise, FR-021); a confirmation records the return
    /// (<see cref="ReturnState.Returned"/>). The use case completes the remedy and registers the serial.
    /// </summary>
    public ReplacementEvent HandToCustomer(DateOnly handedOverOn, bool returnConfirmed, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Require(ReplacementEventType.HandedToCustomer, report);

        var unit = CurrentUnit is { IsReserved: true } reserved
            ? reserved
            : throw new InvalidClaimTransitionException(
                $"Cannot record {ReplacementEventType.HandedToCustomer} on a replacement in status {Status} without a reserved unit.");

        var settled = ReturnState is ReturnState.Returned or ReturnState.HeldByRepairCentre or ReturnState.Accepted;
        var confirmsReturn = ReturnRule == ReplacementReturnRule.AtCollection && !settled;
        if (confirmsReturn && !returnConfirmed)
        {
            throw new ArgumentException(
                "The return of the faulty product must be confirmed to hand the replacement to the customer.", nameof(returnConfirmed));
        }

        unit.End(ReplacementUnitResult.HandedOver, reason: null, report.RecordedAt);
        if (confirmsReturn)
        {
            SetReturnState(ReturnState.Returned, report.RecordedAt);
        }

        SetStatus(ReplacementStatus.HandedToCustomer, report.RecordedAt);
        return Append(
            ReplacementEventType.HandedToCustomer,
            ReplacementStatus.ReadyForCollection,
            ReplacementStatus.HandedToCustomer,
            report,
            new Dictionary<string, object?>
            {
                ["unitId"] = unit.Id,
                ["unitSeq"] = unit.Seq,
                ["serviceCenterId"] = unit.ServiceCenterId,
                ["productId"] = unit.ProductId,
                ["serialNumber"] = unit.SerialNumber,
                ["handedOverOn"] = handedOverOn,
                ["returnConfirmed"] = confirmsReturn,
                ["returnState"] = ReturnState.ToString(),
            });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.NotAvailable"/>: <see cref="ReplacementStatus.AwaitingUnit"/> →
    /// <see cref="ReplacementStatus.NotAvailable"/> (final for the replacement; reason required). The use case evaluates the
    /// <c>ReplacementNotAvailable</c> row and moves the remedy to <c>AwaitingDecision</c>.
    /// </summary>
    public ReplacementEvent MarkNotAvailable(string reason, ReplacementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to record that no replacement is available.", nameof(reason));
        }

        Require(ReplacementEventType.NotAvailable, report);

        SetStatus(ReplacementStatus.NotAvailable, report.RecordedAt);
        return Append(
            ReplacementEventType.NotAvailable,
            ReplacementStatus.AwaitingUnit,
            ReplacementStatus.NotAvailable,
            report,
            new Dictionary<string, object?> { ["reason"] = reason.Trim() });
    }

    /// <summary>
    /// <see cref="ReplacementEventType.Cancelled"/> (system only, from a reviewer's remedy change, research R13): any status
    /// except <see cref="ReplacementStatus.HandedToCustomer"/> and <see cref="ReplacementStatus.Cancelled"/> →
    /// <see cref="ReplacementStatus.Cancelled"/> (final) with a <see cref="ReportSource.System"/> event referring to the remedy
    /// decision; a reserved current unit ends as <see cref="ReplacementUnitResult.Cancelled"/> and the return stays recorded.
    /// A pending model proposal stays undecided: a cancelled replacement allows no event, so it is never decided.
    /// </summary>
    public ReplacementEvent Cancel(Guid remedyDecisionId, DateTimeOffset cancelledAt)
    {
        if (remedyDecisionId == Guid.Empty)
        {
            throw new ArgumentException("The remedy decision that cancels the replacement is required.", nameof(remedyDecisionId));
        }

        if (Status is ReplacementStatus.HandedToCustomer or ReplacementStatus.Cancelled)
        {
            throw new InvalidClaimTransitionException(
                $"Cannot record {ReplacementEventType.Cancelled} on a replacement in status {Status}: the status is final.");
        }

        var from = Status;
        var unit = CurrentUnit is { IsReserved: true } reserved ? reserved : null;
        unit?.End(ReplacementUnitResult.Cancelled, reason: null, cancelledAt);
        SetStatus(ReplacementStatus.Cancelled, cancelledAt);
        return AppendEvent(
            ReplacementEventType.Cancelled,
            from,
            ReplacementStatus.Cancelled,
            reportedAt: cancelledAt,
            recordedAt: cancelledAt,
            ReportSource.System,
            ReplacementEvent.SystemActor,
            note: null,
            payloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["remedyDecisionId"] = remedyDecisionId,
                ["unitId"] = unit?.Id,
                ["unitResult"] = unit?.Result.ToString(),
                ["returnState"] = ReturnState.ToString(),
            }));
    }

    /// <summary>
    /// <see cref="ReplacementEventType.Correction"/> of the staff entry with sequence number <paramref name="correctsSeq"/> (any
    /// status except <see cref="ReplacementStatus.HandedToCustomer"/> and <see cref="ReplacementStatus.Cancelled"/>, reason
    /// required): applies the one corrected value of <paramref name="correction"/> and appends a correction that references the
    /// entry; the corrected entry itself is never changed and no second ready notification is queued for the same unit (FR-017).
    /// Pending T355.
    /// </summary>
    public ReplacementEvent Correct(int correctsSeq, string reason, ReplacementCorrection correction, ReplacementReport report)
        => throw new NotImplementedException($"Pending T355 ({correctsSeq}, {reason}, {correction}, {report}).");

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

    /// <summary>The allowed events as listed in a refusal.</summary>
    internal static string Describe(IReadOnlyList<ReplacementEventType> events)
        => events.Count == 0 ? "none" : string.Join(", ", events);

    /// <summary>Events only a claims reviewer may record (research R5, FR-024).</summary>
    private static bool IsReviewerOnly(ReplacementEventType type)
        => type is ReplacementEventType.ReturnAccepted or ReplacementEventType.ModelApproved or ReplacementEventType.ModelDeclined;

    /// <summary>
    /// Refuses <paramref name="type"/> for the role of <paramref name="report"/> (an auditor records nothing, a claims agent no
    /// reviewer-only event), then unless it is allowed from the current status, naming the status and the allowed events.
    /// </summary>
    private void Require(ReplacementEventType type, ReplacementReport report)
    {
        if (report.Role is not (StaffRole.ClaimsAgent or StaffRole.ClaimsReviewer)
            || (report.Role == StaffRole.ClaimsAgent && IsReviewerOnly(type)))
        {
            throw new UnauthorizedAccessException($"A {report.Role} cannot record {type} on a replacement.");
        }

        var allowed = AllowedEvents(report.Role);
        if (!allowed.Contains(type))
        {
            throw new InvalidClaimTransitionException(
                $"Cannot record {type} on a replacement in status {Status} (return {ReturnState}); allowed events: {Describe(allowed)}.");
        }
    }

    /// <summary>Appends a staff-reported event; the payload also carries the staff member's display name when known.</summary>
    private ReplacementEvent Append(
        ReplacementEventType type,
        ReplacementStatus fromStatus,
        ReplacementStatus toStatus,
        ReplacementReport report,
        Dictionary<string, object?> payload)
    {
        if (!string.IsNullOrWhiteSpace(report.ActorName))
        {
            payload["actorName"] = report.ActorName;
        }

        return AppendEvent(
            type, fromStatus, toStatus, report.ReportedAt, report.RecordedAt, report.Source, report.Actor, report.Note,
            JsonSerializer.Serialize(payload));
    }
}
