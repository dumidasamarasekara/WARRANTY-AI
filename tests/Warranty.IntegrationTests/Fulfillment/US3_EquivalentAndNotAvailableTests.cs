using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Feature 003 user story 3 (quickstart X10 – X12, X19): an equivalent model where the policy allows one, and a
/// replacement that is not available. Aurora (<c>SameCategoryAtLeastClaimValue</c>) lets staff propose another tablet
/// whose claim value is at least the claimed model's (<c>GET /api/claims/{claimId}/replacement-models</c>); the proposal
/// puts the claim in the review queue as a <c>ReplacementCheck</c> item with <c>MODEL_APPROVAL</c>, a unit of it can be
/// reserved only after a reviewer who did not submit the claim approves it (research R5), and the claimant then sees its
/// name. Borealis (<c>SameModel</c>) refuses every other model. Recording <c>NotAvailable</c> ends the replacement and sends
/// the remedy back to a reviewer (<c>REPLACEMENT_NOT_AVAILABLE</c>, row <c>replacementNotAvailable</c>: Refund recommended);
/// the reviewer's confirmation requests the refund (002 R10). Throughout, the claim stays <c>Approved</c> and the remedy
/// history keeps the original Replacement decision; a reviewer's change away from Replacement before the hand-over ends
/// the replacement as <c>Cancelled</c> (research R13).
/// <para>
/// X12-aurora and X12-borealis are seeded for one test each and are changed by it; X10 and X11 are only read (the X10
/// walk-through runs on a copy, so the check of the seeded X10 does not depend on the order the tests run in). The other
/// cases use copies of them (the claim row with its own ID, reference, serial and email, the remedy Replacement decided
/// by a reviewer as the source's first decision and a replacement with the source's rules, initial status and suggested
/// location), as US2's copies of X6; a copy never depends on what another test did to its source.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class US3_EquivalentAndNotAvailableTests(WarrantyAppFixture fixture)
{
    private const string CancellationPending = "Pending T345";
    private const string ReadyTemplate = "ReplacementReadyForCollection";
    private const string ProposalReason = "No Aurora Tab 10 is left in stock; the Tab 11 is its successor.";
    private const string DeclineReason = "A Tab 10 unit arrives at the location next week.";
    private const string NotAvailableReason = "The model is discontinued and no unit is left at any location.";
    private const string RefundReason = "No replacement can be supplied; the policy gives the customer a refund.";
    private const string ChangeReason = "The customer prefers a refund to waiting for a replacement unit.";
    private const string CopyContact = "replacement.equivalent@example.test";
    private const string NotPossibleLabel = "Replacement not possible. Your claim is being reviewed.";

    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;
    private static readonly Guid Borealis = TestStaffUsers.BorealisTenantId;

    /// <summary>Seeded X10-aurora: AUR-TAB10 (AT10-24-0310), Europe, AUR-WP v2; awaiting a unit at Amsterdam.</summary>
    private static readonly Guid X10 = Guid.Parse("11111111-0006-7000-8000-000000000010");

    /// <summary>Seeded X11-aurora: AUR-TAB10 (AT10-24-0311), North America, AUR-WP v2; awaiting a unit at Springfield.</summary>
    private static readonly Guid X11 = Guid.Parse("11111111-0006-7000-8000-000000000011");

    /// <summary>Seeded X12-aurora: AUR-TAB10 (AT10-24-0312), Europe, AUR-WP v2; awaiting a unit at Amsterdam.</summary>
    private static readonly Guid X12Aurora = Guid.Parse("11111111-0006-7000-8000-000000000012");

    /// <summary>Seeded X12-borealis: BOR-SLATE11 (BS11-24-0312), Europe, BOR-WP v1; awaiting the return at Lyon.</summary>
    private static readonly Guid X12Borealis = Guid.Parse("22222222-0006-7000-8000-000000000012");

    /// <summary>The seeded equivalent of AUR-TAB10 (450): same category, claim value 520 (research R14).</summary>
    private static readonly Guid AuroraTab11 = Guid.Parse("11111111-0001-7000-8000-000000000004");

    /// <summary>The seeded Aurora phone (claim value 480): another category than the tablet.</summary>
    private static readonly Guid AuroraPhone6 = Guid.Parse("11111111-0001-7000-8000-000000000002");

    /// <summary>The seeded Borealis Home Hub 2: any model other than the claimed BOR-SLATE11.</summary>
    private static readonly Guid BorealisHub2 = Guid.Parse("22222222-0001-7000-8000-000000000002");

    /// <summary>The AUR-TAB11 catalog serial set aside for X10's unit (aurora serials.json).</summary>
    private const string Tab11Serial = "AT11-25-0001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- X10: an equivalent model approved by a reviewer --------------------------------------------------

    [Fact]
    public async Task X10_the_seeded_Aurora_claim_awaits_a_unit_and_staff_may_propose_another_model()
    {
        var row = await ReplacementRowAsync(X10);
        (row.Status, row.ProductRule, row.ReturnRule, row.ReturnState).ShouldBe(
            ("AwaitingUnit", "SameCategoryAtLeastClaimValue", "AtCollection", "NotYetReturned"));
        (await ScalarAsync<string>("select remedy from claims.claim_remedies where claim_id = @value", X10)).ShouldBe("Replacement");

        var forAgent = await ReplacementAsync(TestStaffUsers.AgentAurora, X10);
        forAgent.TryGetProperty("pendingProposal", out _).ShouldBeFalse();
        forAgent.TryGetProperty("approvedModel", out _).ShouldBeFalse();
        var agentEvents = Strings(forAgent.GetProperty("allowedEvents"));
        agentEvents.ShouldContain("ModelProposed");
        agentEvents.ShouldContain("NotAvailable");
        agentEvents.ShouldNotContain("ModelApproved");
        agentEvents.ShouldNotContain("ModelDeclined");

        // Nothing to decide yet: the reviewer has no approval buttons.
        var reviewerEvents = Strings((await ReplacementAsync(TestStaffUsers.ReviewerAurora, X10)).GetProperty("allowedEvents"));
        reviewerEvents.ShouldContain("ModelProposed");
        reviewerEvents.ShouldNotContain("ModelApproved");
    }

    [Fact]
    public async Task X10_the_proposable_models_are_the_other_tablets_with_at_least_the_claim_value()
    {
        foreach (var user in new[] { TestStaffUsers.AgentAurora, TestStaffUsers.ReviewerAurora, TestStaffUsers.AuditorAurora })
        {
            var models = await ModelsAsync(user, X10);

            var model = models.ShouldHaveSingleItem();
            (model.GetProperty("productId").GetGuid(), model.GetProperty("modelCode").GetString(), model.GetProperty("name").GetString())
                .ShouldBe((AuroraTab11, "AUR-TAB11", "Aurora Tab 11"));
            (model.GetProperty("category").GetString(), model.GetProperty("claimValue").GetDecimal()).ShouldBe(("tablet", 520m));
        }
    }

    [Fact]
    public async Task X10_a_proposed_equivalent_waits_for_the_reviewer_and_after_approval_a_unit_of_it_is_reserved()
    {
        var agent = TestStaffUsers.AgentAurora;
        var reviewer = TestStaffUsers.ReviewerAurora;
        var (claimId, reference) = await CopyAsync(X10);
        var contact = await ContactAsync(claimId);

        // The agent proposes AUR-TAB11 with a reason: the proposal is pending and the status stays.
        var proposed = await RecordAsync(agent, claimId, Propose(AuroraTab11, ProposalReason));

        proposed.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        var pending = proposed.GetProperty("pendingProposal");
        (pending.GetProperty("model").GetProperty("modelCode").GetString(), pending.GetProperty("reason").GetString())
            .ShouldBe(("AUR-TAB11", ProposalReason));
        pending.GetProperty("proposedBy").GetString().ShouldNotBeNullOrWhiteSpace();
        proposed.TryGetProperty("approvedModel", out _).ShouldBeFalse();
        var agentEvents = Strings(proposed.GetProperty("allowedEvents"));
        agentEvents.ShouldNotContain("ModelProposed");
        agentEvents.ShouldNotContain("ModelApproved");
        var recorded = proposed.GetProperty("events").EnumerateArray().Last();
        (recorded.GetProperty("type").GetString(), recorded.GetProperty("fromStatus").GetString(), recorded.GetProperty("toStatus").GetString())
            .ShouldBe(("ModelProposed", "AwaitingUnit", "AwaitingUnit"));
        (await ProposalRowsAsync(claimId)).ShouldBe([new ProposalRow(AuroraTab11, "Pending", ProposalReason, agent.Subject, null, null)]);
        (await TrailAsync(claimId)).Where(e => e.Step == "ReplacementModelProposed").ShouldHaveSingleItem().Actor.ShouldBe(agent.Subject);

        // The reviewers' queue has the claim as a model approval; the reviewer may approve or decline.
        var item = (await ReviewQueueAsync(reviewer)).Single(i => i.GetProperty("claimId").GetGuid() == claimId);
        item.GetProperty("kind").GetString().ShouldBe("ReplacementCheck");
        item.GetProperty("replacementCheckReason").GetString().ShouldBe("MODEL_APPROVAL");
        item.GetProperty("submittedByMe").GetBoolean().ShouldBeFalse();
        var reviewerEvents = Strings((await ReplacementAsync(reviewer, claimId)).GetProperty("allowedEvents"));
        reviewerEvents.ShouldContain("ModelApproved");
        reviewerEvents.ShouldContain("ModelDeclined");

        // Before the approval a unit of AUR-TAB11 cannot be reserved: the unit's model is still the claimed AUR-TAB10 (FR-022).
        var beforeApproval = await ReplacementAsync(agent, claimId);
        var early = await EventAsync(agent, claimId, ETagOf(beforeApproval), Reserve(Tab11Serial));
        early.Status.ShouldBe(HttpStatusCode.BadRequest, early.Body);
        early.Body.ShouldContain("serialNumber");
        (await UnitCountAsync(claimId)).ShouldBe(0);
        (await ReplacementRowAsync(claimId)).Status.ShouldBe("AwaitingUnit");

        // The reviewer approves: AUR-TAB11 becomes the approved model and the claim leaves the queue.
        var approved = await RecordAsync(reviewer, claimId, Event("ModelApproved"));

        approved.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        approved.TryGetProperty("pendingProposal", out _).ShouldBeFalse();
        approved.GetProperty("approvedModel").GetProperty("modelCode").GetString().ShouldBe("AUR-TAB11");
        var approval = (await EventRowsAsync(claimId)).Last();
        (approval.Type, approval.FromStatus, approval.ToStatus, approval.Actor).ShouldBe(
            ("ModelApproved", "AwaitingUnit", "AwaitingUnit", reviewer.Subject));
        (await ProposalRowsAsync(claimId)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            p => p.State.ShouldBe("Approved"), p => p.DecidedBy.ShouldBe(reviewer.Subject));
        (await TrailAsync(claimId)).Where(e => e.Step == "ReplacementModelDecided").ShouldHaveSingleItem().Actor.ShouldBe(reviewer.Subject);
        (await ReviewQueueAsync(reviewer)).ShouldNotContain(i => i.GetProperty("claimId").GetGuid() == claimId);

        // Now the AUR-TAB11 unit is reserved: ready for collection with one keyed notification.
        var ready = await RecordAsync(agent, claimId, Reserve(Tab11Serial));

        ready.GetProperty("status").GetString().ShouldBe("ReadyForCollection");
        ready.GetProperty("currentUnit").GetProperty("modelCode").GetString().ShouldBe("AUR-TAB11");
        (await ScalarAsync<Guid>(
                "select u.product_id from claims.replacement_units u join claims.replacements r on r.id = u.replacement_id where r.claim_id = @value",
                claimId))
            .ShouldBe(AuroraTab11);
        var unitId = await ScalarAsync<Guid>(
            "select u.id from claims.replacement_units u join claims.replacements r on r.id = u.replacement_id where r.claim_id = @value", claimId);
        (await NotificationKeysAsync(claimId)).ShouldBe([$"replacement-ready:{unitId}"]);
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ModelProposed", "ModelApproved", "UnitReserved"]);

        // The claimant sees the model's name, never its serial or the proposal.
        var claimant = await ClaimantViewAsync(WarrantyAppFixture.AuroraHost, reference, contact);
        var remedy = claimant.GetProperty("remedy");
        (remedy.GetProperty("label").GetString(), remedy.GetProperty("replacementModelName").GetString())
            .ShouldBe(("Your replacement is ready for collection", "Aurora Tab 11"));
        var text = claimant.GetRawText();
        text.ShouldNotContain(Tab11Serial, Case.Insensitive);
        text.ShouldNotContain(ProposalReason);
        text.ShouldNotContain(agent.Subject);
        text.ShouldNotContain(reviewer.Subject);

        await ShouldStayApprovedWithTheOriginalReplacementAsync(claimId, seededHistory: false);
    }

    [Fact]
    public async Task X10_only_a_reviewer_decides_a_proposal_and_a_declined_one_keeps_the_claimed_model()
    {
        var agent = TestStaffUsers.AgentAurora;
        var reviewer = TestStaffUsers.ReviewerAurora;
        var (claimId, _) = await CopyAsync(X10);
        await RecordAsync(agent, claimId, Propose(AuroraTab11, ProposalReason));

        // An agent may not decide (403, not 409); a decline needs a reason of at least 10 characters (400 naming reason).
        var forAgent = await ReplacementAsync(agent, claimId);
        foreach (var body in new[] { Event("ModelApproved"), Decline(DeclineReason) })
        {
            var byAgent = await EventAsync(agent, claimId, ETagOf(forAgent), body);
            byAgent.Status.ShouldBe(HttpStatusCode.Forbidden, $"{body["type"]}: {byAgent.Body}");
            byAgent.MediaType.ShouldBe("application/problem+json");
        }

        var forReviewer = await ReplacementAsync(reviewer, claimId);
        foreach (var reason in new[] { null, "No stock." })
        {
            var refused = await EventAsync(reviewer, claimId, ETagOf(forReviewer), Decline(reason));
            refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.Body);
            refused.Body.ShouldContain("reason");
        }

        (await ProposalRowsAsync(claimId)).ShouldHaveSingleItem().State.ShouldBe("Pending");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ModelProposed"]);
        ETagOf(await ReplacementAsync(reviewer, claimId)).ShouldBe(ETagOf(forReviewer));

        // A second proposal while one is pending is not offered.
        var second = await EventAsync(agent, claimId, ETagOf(forAgent), Propose(AuroraTab11, ProposalReason));
        second.Status.ShouldBe(HttpStatusCode.Conflict, second.Body);

        // The reviewer declines with a reason: no model is approved, and another proposal may follow.
        var declined = await RecordAsync(reviewer, claimId, Decline(DeclineReason));

        declined.TryGetProperty("pendingProposal", out _).ShouldBeFalse();
        declined.TryGetProperty("approvedModel", out _).ShouldBeFalse();
        Strings((await ReplacementAsync(agent, claimId)).GetProperty("allowedEvents")).ShouldContain("ModelProposed");
        (await ProposalRowsAsync(claimId)).ShouldBe(
            [new ProposalRow(AuroraTab11, "Declined", ProposalReason, agent.Subject, reviewer.Subject, DeclineReason)]);
        (await EventRowsAsync(claimId)).Last().Payload.ShouldContain(DeclineReason);
        (await ReviewQueueAsync(reviewer)).ShouldNotContain(i => i.GetProperty("claimId").GetGuid() == claimId);

        // A unit of the claimed model is reserved as before.
        var ready = await RecordAsync(agent, claimId, Reserve(NewUnitSerial("AT10")));
        ready.GetProperty("currentUnit").GetProperty("modelCode").GetString().ShouldBe("AUR-TAB10");
        await ShouldStayApprovedWithTheOriginalReplacementAsync(claimId, seededHistory: false);
    }

    // ---- X11: models the policy does not allow -------------------------------------------------------------

    [Fact]
    public async Task X11_a_model_of_another_category_is_refused_naming_the_rule_and_changes_nothing()
    {
        var agent = TestStaffUsers.AgentAurora;
        var before = await ReplacementAsync(agent, X11);
        (await ModelsAsync(agent, X11)).ShouldNotContain(m => m.GetProperty("productId").GetGuid() == AuroraPhone6);

        var response = await EventAsync(agent, X11, ETagOf(before), Propose(AuroraPhone6, ProposalReason));

        response.Status.ShouldBe(HttpStatusCode.BadRequest, response.Body);
        response.MediaType.ShouldBe("application/problem+json");
        response.Body.ShouldContain("productId");
        response.Body.ShouldContain("category", Case.Insensitive);
        (await ProposalRowsAsync(X11)).ShouldBeEmpty();
        (await EventTypesAsync(X11)).ShouldBe(["LocationSuggested"]);
        (await TrailAsync(X11)).ShouldNotContain(e => e.Step == "ReplacementModelProposed");
        (await ReviewQueueAsync(TestStaffUsers.ReviewerAurora)).ShouldNotContain(i => i.GetProperty("claimId").GetGuid() == X11);
        ETagOf(await ReplacementAsync(agent, X11)).ShouldBe(ETagOf(before));
    }

    [Fact]
    public async Task X11_Borealis_allows_the_same_model_only_so_no_model_is_proposable_and_a_proposal_is_refused_naming_the_rule()
    {
        // A copy of X12-borealis with the faulty product returned, so it awaits a unit: only the policy rule refuses the proposal.
        var agent = TestStaffUsers.AgentBorealis;
        var (claimId, _) = await CopyAsync(X12Borealis);
        await RecordAsync(agent, claimId, ReturnRecorded(await ClaimSerialAsync(claimId)));
        var before = await ReplacementAsync(agent, claimId);
        before.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        Strings(before.GetProperty("allowedEvents")).ShouldNotContain("ModelProposed");

        (await ModelsAsync(agent, claimId)).ShouldBeEmpty();

        var response = await EventAsync(agent, claimId, ETagOf(before), Propose(BorealisHub2, ProposalReason));

        response.Status.ShouldBe(HttpStatusCode.BadRequest, response.Body);
        response.MediaType.ShouldBe("application/problem+json");
        response.Body.ShouldContain("productId");
        response.Body.ShouldContain("same model", Case.Insensitive);
        (await ProposalRowsAsync(claimId)).ShouldBeEmpty();
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ReturnRecorded"]);
        ETagOf(await ReplacementAsync(agent, claimId)).ShouldBe(ETagOf(before));
    }

    // ---- X12: replacement not available ----------------------------------------------------------------

    [Fact]
    public async Task X12_not_available_at_Aurora_sends_the_remedy_to_a_reviewer_who_confirms_the_refund_and_the_history_is_kept()
    {
        var agent = TestStaffUsers.AgentAurora;
        var reviewer = TestStaffUsers.ReviewerAurora;

        // A reason of at least 10 characters is required (400 naming reason).
        var before = await ReplacementAsync(agent, X12Aurora);
        var noReason = await EventAsync(agent, X12Aurora, ETagOf(before), NotAvailable(null));
        noReason.Status.ShouldBe(HttpStatusCode.BadRequest, noReason.Body);
        noReason.Body.ShouldContain("reason");
        (await ReplacementRowAsync(X12Aurora)).Status.ShouldBe("AwaitingUnit");

        var notAvailable = await RecordAsync(agent, X12Aurora, NotAvailable(NotAvailableReason));

        await ShouldAwaitTheRefundConfirmationAsync(X12Aurora, notAvailable, agent, reviewer, WarrantyAppFixture.AuroraHost, "AUR-WP");
        await ConfirmRefundAsync(X12Aurora, reviewer, ["LocationSuggested", "NotAvailable"]);
    }

    [Fact]
    public async Task X12_not_available_at_Borealis_after_the_return_sends_the_remedy_to_a_reviewer_who_confirms_the_refund()
    {
        var agent = TestStaffUsers.AgentBorealis;
        var reviewer = TestStaffUsers.ReviewerBorealis;

        // Borealis takes the faulty product back first; "not available" is recorded while awaiting a unit.
        (await ReplacementAsync(agent, X12Borealis)).GetProperty("status").GetString().ShouldBe("AwaitingReturn");
        await RecordAsync(agent, X12Borealis, ReturnRecorded(await ClaimSerialAsync(X12Borealis)));
        var notAvailable = await RecordAsync(agent, X12Borealis, NotAvailable(NotAvailableReason));

        await ShouldAwaitTheRefundConfirmationAsync(
            X12Borealis, notAvailable, agent, reviewer, WarrantyAppFixture.BorealisHost, "BOR-WP");
        (await ReplacementRowAsync(X12Borealis)).ReturnState.ShouldBe("Returned");
        await ConfirmRefundAsync(X12Borealis, reviewer, ["LocationSuggested", "ReturnRecorded", "NotAvailable"]);
    }

    [Fact(Skip = CancellationPending)]
    public async Task X12_a_reviewers_change_from_Replacement_to_Refund_before_the_hand_over_cancels_the_replacement()
    {
        // Borealis's uneconomical-repair row allows Refund or Replacement: the reviewer changes the in-progress Replacement.
        var agent = TestStaffUsers.AgentBorealis;
        var reviewer = TestStaffUsers.ReviewerBorealis;
        var (claimId, _) = await CopyAsync(X12Borealis);
        await RecordAsync(agent, claimId, ReturnRecorded(await ClaimSerialAsync(claimId)));
        await RecordAsync(agent, claimId, Reserve(NewUnitSerial("BS11")));
        var remedy = (await DetailAsync(reviewer, claimId)).GetProperty("remedy");
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Replacement"));

        var changed = await PostAsync(
            reviewer, $"/api/claims/{claimId}/remedy-decisions", remedy.GetProperty("etag").GetString(),
            new JsonObject { ["remedy"] = "Refund", ["reason"] = ChangeReason });

        changed.Status.ShouldBe(HttpStatusCode.Created, changed.Body);
        var detail = await DetailAsync(reviewer, claimId);
        (detail.GetProperty("remedy").GetProperty("state").GetString(), detail.GetProperty("remedy").GetProperty("remedy").GetString())
            .ShouldBe(("InProgress", "Refund"));

        // The replacement ends as Cancelled by the system in the same transaction; its history and the return stay.
        var replacement = detail.GetProperty("replacement");
        replacement.GetProperty("status").GetString().ShouldBe("Cancelled");
        replacement.GetProperty("allowedEvents").GetArrayLength().ShouldBe(0);
        var events = await EventRowsAsync(claimId);
        events.Select(e => e.Type).ShouldBe(["LocationSuggested", "ReturnRecorded", "UnitReserved", "Cancelled"]);
        (events[^1].FromStatus, events[^1].ToStatus, events[^1].Source, events[^1].Actor).ShouldBe(
            ("ReadyForCollection", "Cancelled", "System", "system"));
        (await ScalarAsync<string>(
                "select u.result from claims.replacement_units u join claims.replacements r on r.id = u.replacement_id where r.claim_id = @value",
                claimId))
            .ShouldBe("Cancelled");
        (await ReplacementRowAsync(claimId)).ReturnState.ShouldBe("Returned");
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId)).ShouldBe(WireName.Of(ClaimStatus.Approved));
        (await TrailAsync(claimId)).ShouldContain(e => e.Step == "ActionExecuted" && e.Payload.Contains("request_refund"));
    }

    [Fact]
    public async Task X12_after_the_hand_over_the_remedy_can_no_longer_be_changed_away_from_Replacement()
    {
        var agent = TestStaffUsers.AgentBorealis;
        var reviewer = TestStaffUsers.ReviewerBorealis;
        var (claimId, _) = await CopyAsync(X12Borealis);
        await RecordAsync(agent, claimId, ReturnRecorded(await ClaimSerialAsync(claimId)));
        await RecordAsync(agent, claimId, Reserve(NewUnitSerial("BS11")));
        await RecordAsync(agent, claimId, HandOver());
        var remedy = (await DetailAsync(reviewer, claimId)).GetProperty("remedy");
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("remedy").GetString()).ShouldBe(("Completed", "Replacement"));

        var changed = await PostAsync(
            reviewer, $"/api/claims/{claimId}/remedy-decisions", remedy.GetProperty("etag").GetString(),
            new JsonObject { ["remedy"] = "Refund", ["reason"] = ChangeReason });

        changed.Status.ShouldBe(HttpStatusCode.Conflict, changed.Body);
        (await ReplacementRowAsync(claimId)).Status.ShouldBe("HandedToCustomer");
        (await ScalarAsync<string>("select remedy from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("Replacement");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ReturnRecorded", "UnitReserved", "HandedToCustomer"]);
    }

    // ---- X19: the submitter cannot approve a model -------------------------------------------------------

    [Fact]
    public async Task X19_the_reviewer_who_submitted_the_claim_cannot_approve_or_decline_its_model_proposal()
    {
        var reviewer = TestStaffUsers.ReviewerAurora;
        var (claimId, _) = await CopyAsync(X10, submittedBy: reviewer);
        await RecordAsync(TestStaffUsers.AgentAurora, claimId, Propose(AuroraTab11, ProposalReason));
        var before = await ReplacementAsync(reviewer, claimId);
        var refusedBefore = await SelfReviewRefusedCountAsync(claimId);

        (await ReviewQueueAsync(reviewer)).Single(i => i.GetProperty("claimId").GetGuid() == claimId)
            .GetProperty("submittedByMe").GetBoolean().ShouldBeTrue();

        foreach (var body in new[] { Event("ModelApproved"), Decline(DeclineReason) })
        {
            var response = await EventAsync(reviewer, claimId, ETagOf(before), body);

            response.Status.ShouldBe(HttpStatusCode.Forbidden, $"{body["type"]}: {response.Body}");
            response.MediaType.ShouldBe("application/problem+json");
        }

        (await SelfReviewRefusedCountAsync(claimId)).ShouldBe(refusedBefore + 2);
        (await ScalarAsync<string>(
                $"select actor from audit.security_events where kind = '{WireName.Of(SecurityEventKind.SelfReviewRefused)}' and target = @value limit 1",
                claimId.ToString()))
            .ShouldBe(reviewer.Username);
        (await ProposalRowsAsync(claimId)).ShouldHaveSingleItem().State.ShouldBe("Pending");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ModelProposed"]);
        ETagOf(await ReplacementAsync(reviewer, claimId)).ShouldBe(ETagOf(before));

        // Another reviewer approves it.
        var approved = await RecordAsync(TestStaffUsers.AgentReviewerAurora, claimId, Event("ModelApproved"));
        approved.GetProperty("approvedModel").GetProperty("modelCode").GetString().ShouldBe("AUR-TAB11");
    }

    // ---- Shared expectations -------------------------------------------------------------------------------

    /// <summary>
    /// After <c>NotAvailable</c>: the replacement is final, the remedy awaits a reviewer with <c>REPLACEMENT_NOT_AVAILABLE</c>
    /// and Refund recommended (row <c>replacementNotAvailable</c>), the claim is in the queue as a remedy decision, and the
    /// claimant is told the claim is being reviewed, without the reason.
    /// </summary>
    private async Task ShouldAwaitTheRefundConfirmationAsync(
        Guid claimId, JsonElement notAvailable, TestStaffUser agent, TestStaffUser reviewer, string host, string policyCode)
    {
        notAvailable.GetProperty("status").GetString().ShouldBe("NotAvailable");
        var allowed = Strings(notAvailable.GetProperty("allowedEvents"));
        allowed.ShouldNotContain("UnitReserved");
        allowed.ShouldNotContain("NotAvailable");
        allowed.ShouldNotContain("ModelProposed");
        var recorded = (await EventRowsAsync(claimId)).Last();
        (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Actor).ShouldBe(("NotAvailable", "AwaitingUnit", "NotAvailable", agent.Subject));
        recorded.Payload.ShouldContain(NotAvailableReason);

        var remedy = (await DetailAsync(reviewer, claimId)).GetProperty("remedy");
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("pendingReason").GetString()).ShouldBe(
            ("AwaitingDecision", "REPLACEMENT_NOT_AVAILABLE"));
        remedy.TryGetProperty("remedy", out _).ShouldBeFalse();
        (remedy.GetProperty("scenario").GetString(), remedy.GetProperty("tableRow").GetString()).ShouldBe(
            ("ReplacementNotAvailable", "replacementNotAvailable"));
        Strings(remedy.GetProperty("allowedRemedies")).ShouldBe(["Refund"]);
        remedy.GetProperty("recommendedRemedy").GetString().ShouldBe("Refund");
        remedy.GetProperty("policyVersion").GetProperty("policyCode").GetString().ShouldBe(policyCode);
        var evaluated = remedy.GetProperty("history").EnumerateArray().Last();
        (evaluated.GetProperty("kind").GetString(), evaluated.GetProperty("scenario").GetString()).ShouldBe(("Evaluated", "ReplacementNotAvailable"));

        var item = (await ReviewQueueAsync(reviewer)).Single(i => i.GetProperty("claimId").GetGuid() == claimId);
        (item.GetProperty("kind").GetString(), item.GetProperty("remedyPendingReason").GetString()).ShouldBe(
            ("RemedyDecision", "REPLACEMENT_NOT_AVAILABLE"));

        var claimant = await ClaimantViewAsync(host, await ReferenceAsync(claimId), await ContactAsync(claimId));
        claimant.GetProperty("remedy").GetProperty("label").GetString().ShouldBe(NotPossibleLabel);
        claimant.GetRawText().ShouldNotContain(NotAvailableReason);
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId)).ShouldBe(WireName.Of(ClaimStatus.Approved));
    }

    /// <summary>
    /// The reviewer confirms the recommended Refund with a reason (002 R10): the refund is requested, the claim stays Approved,
    /// the remedy history still starts with the original Replacement decision, and the replacement keeps its events.
    /// </summary>
    private async Task ConfirmRefundAsync(Guid claimId, TestStaffUser reviewer, string[] replacementEvents)
    {
        var remedy = (await DetailAsync(reviewer, claimId)).GetProperty("remedy");
        var confirmed = await PostAsync(
            reviewer, $"/api/claims/{claimId}/remedy-decisions", remedy.GetProperty("etag").GetString(),
            new JsonObject { ["remedy"] = "Refund", ["reason"] = RefundReason });

        confirmed.Status.ShouldBe(HttpStatusCode.Created, confirmed.Body);
        var after = (await DetailAsync(reviewer, claimId)).GetProperty("remedy");
        (after.GetProperty("state").GetString(), after.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Refund"));
        var decided = after.GetProperty("history").EnumerateArray().Last();
        (decided.GetProperty("kind").GetString(), decided.GetProperty("remedy").GetString(), decided.GetProperty("decidedBy").GetString())
            .ShouldBe(("Decided", "Refund", "Reviewer"));
        decided.GetProperty("reason").GetString().ShouldBe(RefundReason);
        (await TrailAsync(claimId)).ShouldContain(e => e.Step == "ActionExecuted" && e.Payload.Contains("request_refund"));
        (await ReviewQueueAsync(reviewer)).ShouldNotContain(i => i.GetProperty("claimId").GetGuid() == claimId);

        var events = await EventTypesAsync(claimId);
        events.Take(replacementEvents.Length).ShouldBe(replacementEvents);
        await ShouldStayApprovedWithTheOriginalReplacementAsync(claimId);
    }

    /// <summary>The claim stays Approved and (for a seeded claim) its remedy history keeps the original Replacement decision.</summary>
    private async Task ShouldStayApprovedWithTheOriginalReplacementAsync(Guid claimId, bool seededHistory = true)
    {
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId)).ShouldBe(WireName.Of(ClaimStatus.Approved));
        if (seededHistory)
        {
            (await ListAsync<string>(
                    "select remedy from claims.remedy_decisions where claim_id = @value and kind = 'Decided' order by seq", claimId))
                .First().ShouldBe("Replacement");
        }
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    /// <summary>
    /// A new claim like <paramref name="source"/> (a seeded US3 claim): a copy of its claim row with its own ID, reference,
    /// serial and email (submitted through the agent portal by <paramref name="submittedBy"/> when given), the remedy
    /// Replacement decided by a reviewer on the source's policy version (as its first decision), and a replacement with the
    /// source's rules, initial status and suggested location (its <c>LocationSuggested</c> event included), as the executor
    /// creates it.
    /// </summary>
    private async Task<(Guid ClaimId, string Reference)> CopyAsync(Guid source, TestStaffUser? submittedBy = null)
    {
        var tenant = source == X12Borealis ? Borealis : Aurora;
        var claimId = Guid.CreateVersion7();
        var reference = ClaimReference.Generate();
        await using (var connection = await OpenAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                create temp table claim_copy on commit drop as select * from claims.claims where id = @source;
                update claim_copy set id = @id, reference = @reference, serial_number = @serial, contact_email = @email;
                update claim_copy set channel = @channel, submitted_by = @submittedBy where @submittedBy <> '';
                insert into claims.claims select * from claim_copy;
                """,
                connection);
            command.Parameters.AddWithValue("source", source);
            command.Parameters.AddWithValue("id", claimId);
            command.Parameters.AddWithValue("reference", reference);
            command.Parameters.AddWithValue("serial", $"{(tenant == Borealis ? "BS11" : "AT10")}-C-{claimId:N}".ToUpperInvariant());
            command.Parameters.AddWithValue("email", CopyContact);
            command.Parameters.AddWithValue("channel", WireName.Of(ClaimChannel.AgentPortal));
            command.Parameters.AddWithValue("submittedBy", submittedBy?.Subject ?? string.Empty);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            command.Transaction = transaction;
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
            await transaction.CommitAsync(Ct);
        }

        // The source as seeded, whatever another test has done to it since: its replacement's initial status (its first event)
        // and the remedy its first reviewer decision chose Replacement from.
        var row = await ReplacementRowAsync(source);
        var initialStatus = await ScalarAsync<string>("select to_status from audit.replacement_events where claim_id = @value and seq = 1", source);
        var (remedyVersionId, scenario, allowed) = await FirstDecisionAsync(source);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        using (TenantContextScope.Begin(tenant, tenant == Borealis ? "borealis" : "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedy = ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), tenant, claimId, remedyVersionId, Enum.Parse<RemedyScenario>(scenario),
                allowed.Select(Enum.Parse<RemedyType>),
                tenant == Borealis ? RemedyPendingReason.RepairNotEconomical : RemedyPendingReason.RepairFailedLimit, at);
            remedy.Decide(RemedyType.Replacement, at);
            scope.ServiceProvider.GetRequiredService<IRemedyRepository>().Add(remedy);
            scope.ServiceProvider.GetRequiredService<IReplacementRepository>().Add(Replacement.Create(
                Guid.CreateVersion7(), tenant, claimId, row.PolicyVersionId, Enum.Parse<ReplacementProductRule>(row.ProductRule),
                Enum.Parse<ReplacementReturnRule>(row.ReturnRule), row.MinimumCoverMonths, Enum.Parse<ReplacementStatus>(initialStatus),
                row.ServiceCenterId, at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        if (submittedBy is not null)
        {
            (await ScalarAsync<string>("select submitted_by from claims.claims where id = @value", claimId)).ShouldBe(submittedBy.Subject);
        }

        return (claimId, reference);
    }

    private static JsonObject Propose(Guid productId, string reason) => Event("ModelProposed", body =>
    {
        body["productId"] = productId;
        body["reason"] = reason;
    });

    private static JsonObject Decline(string? reason) => Event("ModelDeclined", body =>
    {
        if (reason is not null)
        {
            body["reason"] = reason;
        }
    });

    private static JsonObject NotAvailable(string? reason) => Event("NotAvailable", body =>
    {
        if (reason is not null)
        {
            body["reason"] = reason;
        }
    });

    private static JsonObject ReturnRecorded(string serialSeen) => Event("ReturnRecorded", body =>
    {
        body["returnedOn"] = Today();
        body["serialSeen"] = serialSeen;
        body["source"] = "InPerson";
    });

    private static JsonObject Reserve(string serial) => Event("UnitReserved", body => body["serialNumber"] = serial);

    private static JsonObject HandOver() => Event("HandedToCustomer", body =>
    {
        body["source"] = "InPerson";
        body["handedOverOn"] = Today();
    });

    /// <summary>A replacement event request reported by phone a minute ago, adjusted by <paramref name="adjust"/>.</summary>
    private static JsonObject Event(string type, Action<JsonObject>? adjust = null)
    {
        var body = new JsonObject
        {
            ["type"] = type,
            ["reportedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1),
            ["source"] = "Phone",
        };
        adjust?.Invoke(body);
        return body;
    }

    /// <summary>A new unit serial of the model whose serials start with <paramref name="prefix"/>, unused on any claim.</summary>
    private static string NewUnitSerial(string prefix) => $"{prefix}-R-{Guid.CreateVersion7():N}".ToUpperInvariant();

    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string ETagOf(JsonElement view) => view.GetProperty("etag").GetString().ShouldNotBeNull();

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    // ---- API ---------------------------------------------------------------------------------------

    private async Task<JsonElement> DetailAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>The claim detail's <c>replacement</c> (ReplacementView).</summary>
    private async Task<JsonElement> ReplacementAsync(TestStaffUser user, Guid claimId)
        => (await DetailAsync(user, claimId)).GetProperty("replacement");

    /// <summary><c>GET /api/claims/{claimId}/replacement-models</c> (200).</summary>
    private async Task<List<JsonElement>> ModelsAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}/replacement-models", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();
    }

    private Task<PostResponse> EventAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
        => PostAsync(user, $"/api/claims/{claimId}/replacement/events", ifMatch, body);

    /// <summary>Records <paramref name="body"/> with the replacement's current ETag and returns the updated replacement (201).</summary>
    private async Task<JsonElement> RecordAsync(TestStaffUser user, Guid claimId, JsonObject body)
    {
        var response = await EventAsync(user, claimId, ETagOf(await ReplacementAsync(user, claimId)), body);
        response.Status.ShouldBe(HttpStatusCode.Created, $"{body["type"]}: {response.Body}");
        return JsonSerializer.Deserialize<JsonElement>(response.Body);
    }

    private async Task<PostResponse> PostAsync(TestStaffUser user, string path, string? ifMatch, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await client.SendAsync(request, Ct);
        return new PostResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<List<JsonElement>> ReviewQueueAsync(TestStaffUser user)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync("/api/review-queue", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();
    }

    /// <summary>The claimant view on <paramref name="host"/>, opened with the reference and the claim's contact.</summary>
    private async Task<JsonElement> ClaimantViewAsync(string host, string reference, string contact)
    {
        using var client = fixture.CreateClaimantClient(host);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(host, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<ReplacementRow> ReplacementRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select r.status, r.product_rule, r.return_rule, r.return_state, r.minimum_cover_months, r.policy_version_id,
                   l.service_center_id, m.policy_version_id, m.scenario, m.allowed_remedies
            from claims.replacements r
            left join claims.replacement_locations l on l.id = r.active_location_id
            join claims.claim_remedies m on m.claim_id = r.claim_id
            where r.claim_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no replacement");
        return new ReplacementRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetGuid(7), reader.GetString(8), reader.GetFieldValue<string[]>(9));
    }

    /// <summary>The policy version, scenario and allowed remedies of the claim's first <c>Decided</c> remedy decision.</summary>
    private async Task<(Guid PolicyVersionId, string Scenario, string[] Allowed)> FirstDecisionAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select policy_version_id, scenario, allowed_remedies from claims.remedy_decisions
            where claim_id = @id and kind = 'Decided' order by seq limit 1
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no remedy decision");
        return (reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<string[]>(2));
    }

    /// <summary>The claim's model proposals in the order they were made.</summary>
    private async Task<List<ProposalRow>> ProposalRowsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select p.product_id, p.state, p.reason, p.proposed_by, p.decided_by, p.decision_reason
            from claims.replacement_model_proposals p join claims.replacements r on r.id = p.replacement_id
            where r.claim_id = @id order by p.proposed_at, p.id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<ProposalRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new ProposalRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private Task<List<string>> EventTypesAsync(Guid claimId)
        => ListAsync<string>("select type from audit.replacement_events where claim_id = @value order by seq", claimId);

    private async Task<List<EventRow>> EventRowsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select seq, type, from_status, to_status, source, actor, coalesce(payload::text, '')
            from audit.replacement_events where claim_id = @id order by seq
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<EventRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new EventRow(
                reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }

        return rows;
    }

    private async Task<List<TrailRow>> TrailAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select step, actor, coalesce(payload::text, '') from audit.decision_trail_entries where claim_id = @id order by seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<TrailRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new TrailRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private Task<long> UnitCountAsync(Guid claimId)
        => ScalarAsync<long>(
            "select count(*) from claims.replacement_units u join claims.replacements r on r.id = u.replacement_id where r.claim_id = @value",
            claimId);

    /// <summary>The dedupe keys of the claim's ready notifications.</summary>
    private Task<List<string>> NotificationKeysAsync(Guid claimId)
        => ListAsync<string>(
            $"select coalesce(dedupe_key, '') from integration.notifications where claim_id = @value and template = '{ReadyTemplate}'", claimId);

    private Task<long> SelfReviewRefusedCountAsync(Guid claimId)
        => ScalarAsync<long>(
            $"select count(*) from audit.security_events where kind = '{WireName.Of(SecurityEventKind.SelfReviewRefused)}' and target = @value",
            claimId.ToString());

    private Task<string> ClaimSerialAsync(Guid claimId)
        => ScalarAsync<string>("select serial_number from claims.claims where id = @value", claimId);

    private Task<string> ReferenceAsync(Guid claimId)
        => ScalarAsync<string>("select reference from claims.claims where id = @value", claimId);

    private Task<string> ContactAsync(Guid claimId)
        => ScalarAsync<string>("select contact_email from claims.claims where id = @value", claimId);

    /// <summary>Runs <paramref name="sql"/> with <paramref name="value"/> as <c>@value</c> and returns the single result.</summary>
    private async Task<T> ScalarAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return await command.ExecuteScalarAsync(Ct) is T result ? result : throw new ShouldAssertException($"'{sql}' returned no {typeof(T).Name}.");
    }

    private async Task<List<T>> ListAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record PostResponse(HttpStatusCode Status, string? MediaType, string Body);

    private sealed record ReplacementRow(
        string Status,
        string ProductRule,
        string ReturnRule,
        string ReturnState,
        int MinimumCoverMonths,
        Guid PolicyVersionId,
        Guid? ServiceCenterId,
        Guid RemedyPolicyVersionId,
        string RemedyScenario,
        string[] AllowedRemedies);

    private sealed record ProposalRow(Guid ProductId, string State, string Reason, string ProposedBy, string? DecidedBy, string? DecisionReason);

    private sealed record EventRow(int Seq, string Type, string? FromStatus, string? ToStatus, string Source, string Actor, string Payload);

    private sealed record TrailRow(string Step, string Actor, string Payload);
}
