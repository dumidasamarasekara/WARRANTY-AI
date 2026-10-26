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
/// Feature 003 user story 2 (quickstart X6 – X9): the faulty product is returned as the claim's policy requires. A
/// Borealis replacement (<c>BeforeReservation</c>) starts at <c>AwaitingReturn</c>; staff record the return with the serial
/// seen through <c>POST /api/claims/{claimId}/replacement/events</c> (<c>ReturnRecorded</c>, T337). A serial that does not
/// match the claim's (001 FR-016 normalisation) keeps the replacement where it is with the return state <c>Mismatch</c>,
/// puts the claim in the review queue as a <c>ReplacementCheck</c> item with <c>RETURN_MISMATCH</c> (T338) and blocks the
/// reservation and the hand-over until a reviewer who did not submit the claim accepts the return with a reason
/// (<c>ReturnAccepted</c>, research R5). A replacement that follows a repair whose centre still holds the product starts
/// with the return recorded by the system, referring to the repair hand-over (research R9).
/// <para>
/// X7, X8 and X9 are seeded for one test each and are changed by it. X6 is shared (US1's cross-tenant checks read it), so
/// it only gets reads and refused requests; the other mismatch cases use copies of X6 (or of US1's X1 for the
/// <c>AtCollection</c> rule): the claim row with its own ID, reference, serial and email, and through the Application ports
/// the remedy Replacement decided by a reviewer and a replacement with the source's rules, status and suggested location.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class US2_FaultyProductReturnTests(WarrantyAppFixture fixture)
{
    private const string ReadyTemplate = "ReplacementReadyForCollection";
    private const string ReturnNote = "Returned at the counter in its original box; staff-only note.";
    private const string AcceptReason = "The claim's serial was mistyped at intake; the returned unit is the customer's.";
    private const string DecisionReason = "Two repairs failed; the Aurora policy gives the customer a replacement.";
    private const string CopyContact = "replacement.return@example.test";
    private const string BringLabel = "Please bring your product to the location below";
    private const string PreparingLabel = "We are preparing your replacement";

    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;
    private static readonly Guid Borealis = TestStaffUsers.BorealisTenantId;

    /// <summary>Seeded X6-borealis: BOR-SLATE11 (BS11-24-0306), Europe, BOR-WP v1; awaiting the return at Lyon. Shared.</summary>
    private static readonly Guid X6 = Guid.Parse("22222222-0006-7000-8000-000000000006");

    /// <summary>Seeded X7-borealis: BOR-SLATE11 (BS11-24-0307), as X6; returned with its own serial, reserved, handed over.</summary>
    private static readonly Guid X7 = Guid.Parse("22222222-0006-7000-8000-000000000007");

    /// <summary>Seeded X8-borealis: BOR-SLATE11 (BS11-24-0308), as X6; returned with another serial, accepted by a reviewer.</summary>
    private static readonly Guid X8 = Guid.Parse("22222222-0006-7000-8000-000000000008");

    /// <summary>Seeded X9-aurora: AUR-TAB10 (AT10-24-0309), North America, AUR-WP v2; repair awaiting hand-over at Springfield.</summary>
    private static readonly Guid X9 = Guid.Parse("11111111-0006-7000-8000-000000000009");

    /// <summary>A serial of the same model that is not X8's (it is X7's claim serial).</summary>
    private const string OtherSerial = "BS11-24-0307";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- X6: the seeded return-first replacement ---------------------------------------------------------

    [Fact]
    public async Task X6_the_seeded_Borealis_claim_awaits_the_return_and_the_claimant_is_asked_to_bring_the_product()
    {
        var expected = await FirstReplacementLocationAsync(Borealis, "EU");
        var expectedName = await ScalarAsync<string>("select name from integration.service_centers where id = @value", expected);
        expectedName.ShouldBe("Borealis Repair Hub Lyon");

        var row = await ReplacementRowAsync(X6);
        (row.Status, row.ProductRule, row.ReturnRule, row.ReturnState, row.MinimumCoverMonths).ShouldBe(
            ("AwaitingReturn", "SameModel", "BeforeReservation", "NotYetReturned", 6));
        (row.ServiceCenterId, row.Assignment).ShouldBe((expected, "Suggested"));
        (await ReturnRowAsync(X6)).ShouldBeNull();

        // Staff may record the return, not a unit; no return is shown yet.
        var replacement = await ReplacementAsync(TestStaffUsers.AgentBorealis, X6);
        replacement.GetProperty("status").GetString().ShouldBe("AwaitingReturn");
        replacement.GetProperty("returnState").GetString().ShouldBe("NotYetReturned");
        replacement.TryGetProperty("faultyProductReturn", out _).ShouldBeFalse();
        var allowed = Strings(replacement.GetProperty("allowedEvents"));
        allowed.ShouldContain("ReturnRecorded");
        allowed.ShouldNotContain("UnitReserved");
        allowed.ShouldNotContain("HandedToCustomer");
        allowed.ShouldNotContain("ReturnAccepted");

        // The claimant is asked to bring the product to the location, with its public details only.
        var claimant = await ClaimantViewAsync(WarrantyAppFixture.BorealisHost, await ReferenceAsync(X6), await ContactAsync(X6));
        var remedy = claimant.GetProperty("remedy");
        (remedy.GetProperty("remedy").GetString(), remedy.GetProperty("label").GetString()).ShouldBe(("Replacement", BringLabel));
        remedy.GetProperty("completed").GetBoolean().ShouldBeFalse();
        var location = remedy.GetProperty("location");
        location.GetProperty("name").GetString().ShouldBe(expectedName);
        location.GetProperty("address").GetString().ShouldNotBeNullOrWhiteSpace();
        location.GetProperty("openingHours").GetString().ShouldNotBeNullOrWhiteSpace();
        location.TryGetProperty("contact", out _).ShouldBeFalse();
        Steps(remedy).ShouldBe([("Product returned", "current"), ("Ready for collection", "upcoming"), ("Handed to you", "upcoming")]);
        claimant.GetRawText().ShouldNotContain(await ClaimSerialAsync(X6), Case.Insensitive);
    }

    [Fact]
    public async Task X6_reserving_a_unit_before_the_return_conflicts_naming_AwaitingReturn_and_changes_nothing()
    {
        var before = await ReplacementAsync(TestStaffUsers.AgentBorealis, X6);
        var eventsBefore = await EventTypesAsync(X6);

        var response = await EventAsync(TestStaffUsers.AgentBorealis, X6, ETagOf(before), Reserve(NewUnitSerial("BS11")));

        response.Status.ShouldBe(HttpStatusCode.Conflict, response.Body);
        response.MediaType.ShouldBe("application/problem+json");
        response.Body.ShouldContain("AwaitingReturn");
        response.Body.ShouldContain("ReturnRecorded");
        (await ReplacementRowAsync(X6)).Status.ShouldBe("AwaitingReturn");
        (await EventTypesAsync(X6)).ShouldBe(eventsBefore);
        (await UnitCountAsync(X6)).ShouldBe(0);
        (await NotificationKeysAsync(X6)).ShouldBeEmpty();
        ETagOf(await ReplacementAsync(TestStaffUsers.AgentBorealis, X6)).ShouldBe(ETagOf(before));
    }

    // ---- X7: return, then reservation and hand-over ------------------------------------------------------

    [Fact(Skip = "Pending T337")]
    public async Task X7_the_return_with_the_claims_serial_lets_a_unit_be_reserved_and_handed_over_without_a_return_confirmation()
    {
        var agent = TestStaffUsers.AgentBorealis;
        var lyon = (await ReplacementRowAsync(X7)).ServiceCenterId.ShouldNotBeNull();
        var notificationsBefore = await NotificationCountAsync(X7);
        var reference = await ReferenceAsync(X7);
        var contact = await ContactAsync(X7);

        // The serial seen is the claim's (BS11-24-0307) in another spelling: it matches after the 001 FR-016 normalisation.
        var returned = await RecordAsync(agent, X7, ReturnRecorded("  bs11 24 0307 ", note: ReturnNote));

        returned.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        returned.GetProperty("returnState").GetString().ShouldBe("Returned");
        var faulty = returned.GetProperty("faultyProductReturn");
        (faulty.GetProperty("returnedOn").GetString(), faulty.GetProperty("serialSeen").GetString()).ShouldBe((Today(), "bs11 24 0307"));
        (faulty.GetProperty("serialMatches").GetBoolean(), faulty.GetProperty("heldByRepairCentre").GetBoolean()).ShouldBe((true, false));
        var allowed = Strings(returned.GetProperty("allowedEvents"));
        allowed.ShouldContain("UnitReserved");
        allowed.ShouldNotContain("ReturnRecorded");
        var recorded = returned.GetProperty("events").EnumerateArray().Last();
        (recorded.GetProperty("type").GetString(), recorded.GetProperty("fromStatus").GetString(), recorded.GetProperty("toStatus").GetString())
            .ShouldBe(("ReturnRecorded", "AwaitingReturn", "AwaitingUnit"));

        // Stored at the active location with the agent; one trail entry; no customer notification for a return.
        (await ReturnRowAsync(X7)).ShouldBe(new ReturnRow(
            DateOnly.FromDateTime(DateTime.UtcNow), lyon, "bs11 24 0307", true, null, ReturnNote, agent.Subject));
        var trailEntry = (await TrailAsync(X7)).Where(e => e.Step == "FaultyProductReturned").ShouldHaveSingleItem();
        trailEntry.Actor.ShouldBe(agent.Subject);
        trailEntry.Payload.ShouldContain("\"serialMatches\": true");
        (await NotificationCountAsync(X7)).ShouldBe(notificationsBefore);

        var afterReturn = (await ClaimantViewAsync(WarrantyAppFixture.BorealisHost, reference, contact)).GetProperty("remedy");
        afterReturn.GetProperty("label").GetString().ShouldBe(PreparingLabel);
        Steps(afterReturn).ShouldBe([("Product returned", "done"), ("Ready for collection", "current"), ("Handed to you", "upcoming")]);

        // A unit of the same model is reserved: ready, one keyed notification.
        var serial = NewUnitSerial("BS11");
        var ready = await RecordAsync(agent, X7, Reserve(serial));
        ready.GetProperty("status").GetString().ShouldBe("ReadyForCollection");
        Strings(ready.GetProperty("allowedEvents")).ShouldContain("HandedToCustomer");
        var unitId = await ScalarAsync<Guid>(
            "select u.id from claims.replacement_units u join claims.replacements r on r.id = u.replacement_id where r.claim_id = @value", X7);
        (await NotificationKeysAsync(X7)).ShouldBe([$"replacement-ready:{unitId}"]);

        var readyView = await ClaimantViewAsync(WarrantyAppFixture.BorealisHost, reference, contact);
        readyView.GetProperty("remedy").GetProperty("label").GetString().ShouldBe("Your replacement is ready for collection");
        Steps(readyView.GetProperty("remedy")).ShouldBe(
            [("Product returned", "done"), ("Ready for collection", "current"), ("Handed to you", "upcoming")]);

        // BeforeReservation: the hand-over asks no return confirmation; it completes the remedy and registers the serial.
        var handedOver = await RecordAsync(agent, X7, HandOver(returnConfirmed: null));
        handedOver.GetProperty("status").GetString().ShouldBe("HandedToCustomer");
        handedOver.GetProperty("returnState").GetString().ShouldBe("Returned");
        handedOver.GetProperty("allowedEvents").GetArrayLength().ShouldBe(0);
        (await EventTypesAsync(X7)).ShouldBe(["LocationSuggested", "ReturnRecorded", "UnitReserved", "HandedToCustomer"]);
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", X7)).ShouldBe("Completed");
        (await ScalarAsync<string>("select status from claims.claims where id = @value", X7)).ShouldBe(WireName.Of(ClaimStatus.Approved));
        (await ScalarAsync<string>("select original_serial from catalog.replacement_serials where serial_number = @value", serial))
            .ShouldBe("BS11-24-0307");
        (await NotificationKeysAsync(X7)).ShouldHaveSingleItem();

        var claimant = await ClaimantViewAsync(WarrantyAppFixture.BorealisHost, reference, contact);
        var remedy = claimant.GetProperty("remedy");
        (remedy.GetProperty("label").GetString(), remedy.GetProperty("completed").GetBoolean()).ShouldBe(("Replacement handed over", true));
        Steps(remedy).ShouldBe([("Product returned", "done"), ("Ready for collection", "done"), ("Handed to you", "done")]);
        ShouldShowNoStaffData(claimant, agent, serial, "bs11 24 0307", ReturnNote);
    }

    // ---- X8: a returned product that does not match ----------------------------------------------------

    [Fact(Skip = "Pending T338")]
    public async Task X8_a_mismatching_return_stays_awaiting_return_goes_to_the_review_queue_and_the_reviewer_accepts_it()
    {
        var agent = TestStaffUsers.AgentBorealis;
        var reviewer = TestStaffUsers.ReviewerBorealis;
        var notificationsBefore = await NotificationCountAsync(X8);

        var mismatch = await RecordAsync(agent, X8, ReturnRecorded(OtherSerial, note: ReturnNote));

        // The return is stored with the mismatch; the replacement does not proceed.
        mismatch.GetProperty("status").GetString().ShouldBe("AwaitingReturn");
        mismatch.GetProperty("returnState").GetString().ShouldBe("Mismatch");
        var faulty = mismatch.GetProperty("faultyProductReturn");
        (faulty.GetProperty("serialSeen").GetString(), faulty.GetProperty("serialMatches").GetBoolean()).ShouldBe((OtherSerial, false));
        var recorded = mismatch.GetProperty("events").EnumerateArray().Last();
        (recorded.GetProperty("type").GetString(), recorded.GetProperty("fromStatus").GetString(), recorded.GetProperty("toStatus").GetString())
            .ShouldBe(("ReturnRecorded", "AwaitingReturn", "AwaitingReturn"));
        (await ReplacementRowAsync(X8)).ReturnState.ShouldBe("Mismatch");
        (await ReturnRowAsync(X8)).ShouldNotBeNull().SerialMatches.ShouldBeFalse();
        (await TrailAsync(X8)).Where(e => e.Step == "FaultyProductReturned").ShouldHaveSingleItem().Payload
            .ShouldContain("\"serialMatches\": false");
        (await NotificationCountAsync(X8)).ShouldBe(notificationsBefore);

        // The reviewers' queue has the claim as a replacement check (T338); the claimant sees no check result.
        var item = (await ReviewQueueAsync(reviewer)).Single(i => i.GetProperty("claimId").GetGuid() == X8);
        item.GetProperty("kind").GetString().ShouldBe("ReplacementCheck");
        item.GetProperty("replacementCheckReason").GetString().ShouldBe("RETURN_MISMATCH");
        item.GetProperty("reference").GetString().ShouldBe(await ReferenceAsync(X8));
        item.GetProperty("submittedByMe").GetBoolean().ShouldBeFalse();
        item.GetProperty("escalationReasons").EnumerateArray().ShouldHaveSingleItem().GetString()
            .ShouldNotBeNull().ShouldContain("does not match", Case.Insensitive);

        var reference = await ReferenceAsync(X8);
        var contact = await ContactAsync(X8);
        var waiting = await ClaimantViewAsync(WarrantyAppFixture.BorealisHost, reference, contact);
        waiting.GetProperty("remedy").GetProperty("label").GetString().ShouldBe(BringLabel);
        ShouldShowNoStaffData(waiting, agent, OtherSerial, ReturnNote);

        // The reviewer accepts the return with a reason: the replacement waits for a unit and leaves the queue.
        var accepted = await RecordAsync(reviewer, X8, Accept(AcceptReason));

        accepted.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        accepted.GetProperty("returnState").GetString().ShouldBe("Accepted");
        accepted.GetProperty("faultyProductReturn").GetProperty("serialMatches").GetBoolean().ShouldBeFalse();
        Strings(accepted.GetProperty("allowedEvents")).ShouldContain("UnitReserved");
        var acceptEvent = accepted.GetProperty("events").EnumerateArray().Last();
        (acceptEvent.GetProperty("type").GetString(), acceptEvent.GetProperty("fromStatus").GetString(), acceptEvent.GetProperty("toStatus").GetString())
            .ShouldBe(("ReturnAccepted", "AwaitingReturn", "AwaitingUnit"));
        (await TrailAsync(X8)).ShouldContain(e => e.Actor == reviewer.Subject && e.Payload.Contains("ReturnAccepted") && e.Payload.Contains(AcceptReason));
        (await ReviewQueueAsync(reviewer)).ShouldNotContain(i => i.GetProperty("claimId").GetGuid() == X8);

        var claimant = await ClaimantViewAsync(WarrantyAppFixture.BorealisHost, reference, contact);
        claimant.GetProperty("remedy").GetProperty("label").GetString().ShouldBe(PreparingLabel);
        Steps(claimant.GetProperty("remedy")).ShouldBe(
            [("Product returned", "done"), ("Ready for collection", "current"), ("Handed to you", "upcoming")]);
        ShouldShowNoStaffData(claimant, reviewer, OtherSerial, AcceptReason);
    }

    [Fact(Skip = "Pending T337")]
    public async Task X8_an_agent_cannot_accept_a_mismatching_return_and_a_reviewer_accepts_it_only_with_a_reason()
    {
        var (claimId, _) = await CopyOfX6Async();
        await RecordAsync(TestStaffUsers.AgentBorealis, claimId, ReturnRecorded(OtherSerial));

        // The agent sees no Accept return and is refused with 403, not 409; nothing changes.
        var forAgent = await ReplacementAsync(TestStaffUsers.AgentBorealis, claimId);
        Strings(forAgent.GetProperty("allowedEvents")).ShouldNotContain("ReturnAccepted");
        var byAgent = await EventAsync(TestStaffUsers.AgentBorealis, claimId, ETagOf(forAgent), Accept(AcceptReason));
        byAgent.Status.ShouldBe(HttpStatusCode.Forbidden, byAgent.Body);
        byAgent.MediaType.ShouldBe("application/problem+json");

        // A reviewer needs a reason of at least 10 characters (400 naming reason).
        var forReviewer = await ReplacementAsync(TestStaffUsers.ReviewerBorealis, claimId);
        Strings(forReviewer.GetProperty("allowedEvents")).ShouldContain("ReturnAccepted");
        foreach (var reason in new[] { null, "Typo." })
        {
            var refused = await EventAsync(TestStaffUsers.ReviewerBorealis, claimId, ETagOf(forReviewer), Accept(reason));
            refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.Body);
            refused.Body.ShouldContain("reason");
        }

        var refusedRow = await ReplacementRowAsync(claimId);
        (refusedRow.Status, refusedRow.ReturnState).ShouldBe(("AwaitingReturn", "Mismatch"));
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ReturnRecorded"]);
        ETagOf(await ReplacementAsync(TestStaffUsers.ReviewerBorealis, claimId)).ShouldBe(ETagOf(forReviewer));

        var accepted = await RecordAsync(TestStaffUsers.ReviewerBorealis, claimId, Accept(AcceptReason));

        (accepted.GetProperty("status").GetString(), accepted.GetProperty("returnState").GetString()).ShouldBe(("AwaitingUnit", "Accepted"));
        var row = await ReplacementRowAsync(claimId);
        (row.Status, row.ReturnState).ShouldBe(("AwaitingUnit", "Accepted"));
        (await ReturnRowAsync(claimId)).ShouldNotBeNull().SerialMatches.ShouldBeFalse();
        var acceptRow = (await EventRowsAsync(claimId)).Last();
        (acceptRow.Type, acceptRow.FromStatus, acceptRow.ToStatus, acceptRow.Actor).ShouldBe(
            ("ReturnAccepted", "AwaitingReturn", "AwaitingUnit", TestStaffUsers.ReviewerBorealis.Subject));
        acceptRow.Payload.ShouldContain(AcceptReason);

        // Now a unit can be reserved.
        (await RecordAsync(TestStaffUsers.AgentBorealis, claimId, Reserve(NewUnitSerial("BS11")))).GetProperty("status").GetString()
            .ShouldBe("ReadyForCollection");
    }

    [Fact(Skip = "Pending T337")]
    public async Task X8_while_the_return_does_not_match_no_unit_can_be_reserved_and_the_return_cannot_be_recorded_again()
    {
        var (claimId, _) = await CopyOfX6Async();
        await RecordAsync(TestStaffUsers.AgentBorealis, claimId, ReturnRecorded(OtherSerial));
        var before = await ReplacementAsync(TestStaffUsers.AgentBorealis, claimId);
        var allowed = Strings(before.GetProperty("allowedEvents"));
        allowed.ShouldNotContain("UnitReserved");
        allowed.ShouldNotContain("ReturnRecorded");
        allowed.ShouldNotContain("HandedToCustomer");

        var claimSerial = await ClaimSerialAsync(claimId);
        foreach (var body in new[] { Reserve(NewUnitSerial("BS11")), ReturnRecorded(claimSerial), HandOver(returnConfirmed: true) })
        {
            var refused = await EventAsync(TestStaffUsers.AgentBorealis, claimId, ETagOf(before), body);
            refused.Status.ShouldBe(HttpStatusCode.Conflict, $"{body["type"]}: {refused.Body}");
            refused.Body.ShouldContain("AwaitingReturn");
        }

        var row = await ReplacementRowAsync(claimId);
        (row.Status, row.ReturnState).ShouldBe(("AwaitingReturn", "Mismatch"));
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ReturnRecorded"]);
        (await UnitCountAsync(claimId)).ShouldBe(0);
        (await NotificationKeysAsync(claimId)).ShouldBeEmpty();
        ETagOf(await ReplacementAsync(TestStaffUsers.AgentBorealis, claimId)).ShouldBe(ETagOf(before));
    }

    [Fact(Skip = "Pending T337")]
    public async Task X8_the_reviewer_who_submitted_the_claim_cannot_accept_its_return()
    {
        var reviewer = TestStaffUsers.ReviewerBorealis;
        var (claimId, _) = await CopyOfX6Async(submittedBy: reviewer);
        await RecordAsync(TestStaffUsers.AgentBorealis, claimId, ReturnRecorded(OtherSerial));
        var before = await ReplacementAsync(reviewer, claimId);
        var refusedBefore = await SelfReviewRefusedCountAsync(claimId);

        var response = await EventAsync(reviewer, claimId, ETagOf(before), Accept(AcceptReason));

        response.Status.ShouldBe(HttpStatusCode.Forbidden, response.Body);
        response.MediaType.ShouldBe("application/problem+json");
        (await SelfReviewRefusedCountAsync(claimId)).ShouldBe(refusedBefore + 1);
        (await ScalarAsync<string>(
                $"select actor from audit.security_events where kind = '{WireName.Of(SecurityEventKind.SelfReviewRefused)}' and target = @value",
                claimId.ToString()))
            .ShouldBe(reviewer.Username);
        var row = await ReplacementRowAsync(claimId);
        (row.Status, row.ReturnState).ShouldBe(("AwaitingReturn", "Mismatch"));
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "ReturnRecorded"]);
        ETagOf(await ReplacementAsync(reviewer, claimId)).ShouldBe(ETagOf(before));
    }

    [Fact(Skip = "Pending T337")]
    public async Task X8_under_AtCollection_a_mismatching_return_blocks_the_hand_over_until_a_reviewer_accepts_it()
    {
        // A copy of US1's X1 (Aurora, returned at collection) with a reserved unit.
        var agent = TestStaffUsers.AgentAurora;
        var (claimId, _) = await new US1_ReplacementCollectionTests(fixture).CopyOfX1Async();
        await RecordAsync(agent, claimId, Reserve(NewUnitSerial("AT10")));

        var mismatch = await RecordAsync(agent, claimId, ReturnRecorded("AT10-24-9001"));

        (mismatch.GetProperty("status").GetString(), mismatch.GetProperty("returnState").GetString()).ShouldBe(("ReadyForCollection", "Mismatch"));
        Strings(mismatch.GetProperty("allowedEvents")).ShouldNotContain("HandedToCustomer");

        // Even with the return confirmed, the hand-over waits for the reviewer.
        var refused = await EventAsync(agent, claimId, ETagOf(mismatch), HandOver(returnConfirmed: true));
        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.Body);
        refused.Body.ShouldContain("ReadyForCollection");
        (await ReplacementRowAsync(claimId)).Status.ShouldBe("ReadyForCollection");
        (await ScalarAsync<long>("select count(*) from catalog.replacement_serials where claim_id = @value", claimId)).ShouldBe(0);

        // The reviewer accepts; the status stays and the hand-over needs no further confirmation.
        var accepted = await RecordAsync(TestStaffUsers.ReviewerAurora, claimId, Accept(AcceptReason));
        (accepted.GetProperty("status").GetString(), accepted.GetProperty("returnState").GetString()).ShouldBe(("ReadyForCollection", "Accepted"));
        Strings(accepted.GetProperty("allowedEvents")).ShouldContain("HandedToCustomer");

        var handedOver = await RecordAsync(agent, claimId, HandOver(returnConfirmed: null));
        (handedOver.GetProperty("status").GetString(), handedOver.GetProperty("returnState").GetString())
            .ShouldBe(("HandedToCustomer", "Accepted"));
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("Completed");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "UnitReserved", "ReturnRecorded", "ReturnAccepted", "HandedToCustomer"]);
    }

    // ---- X9: the product is already with the repair centre ----------------------------------------------

    [Fact(Skip = "Pending T337")]
    public async Task X9_a_replacement_after_two_failed_repairs_starts_with_the_product_held_by_the_repair_centre()
    {
        var agent = TestStaffUsers.AgentAurora;
        var reviewer = TestStaffUsers.ReviewerAurora;

        // 002: hand-over, failure, new attempt at the centre, second failure; the remedy awaits the reviewer (Replacement).
        var repair = (await DetailAsync(agent, X9)).GetProperty("repair");
        repair.GetProperty("status").GetString().ShouldBe("AwaitingHandover");
        await RecordRepairAsync(agent, Event("HandedOver", body =>
        {
            body["reportedAt"] = DateTimeOffset.UtcNow.AddMinutes(-30);
            body["handedOverOn"] = Today();
            body["centreReference"] = "JOB-0309";
        }));
        await RecordRepairAsync(agent, Event("RepairFailed", body => body["reason"] = "The tablet still restarts after the board was reseated."));
        await RecordRepairAsync(agent, Event("NewAttemptStarted", body => body["productLocation"] = "AtCentre"));
        var limit = await RecordRepairAsync(agent, Event("RepairFailed", body => body["reason"] = "The replacement board shows the same restarts."));
        limit.GetProperty("status").GetString().ShouldBe("RepairNotPossible");
        var handoverId = await ScalarAsync<Guid>(
            "select h.id from claims.repair_handovers h join claims.repairs r on r.id = h.repair_id where r.claim_id = @value and h.is_active",
            X9);

        var remedy = (await DetailAsync(reviewer, X9)).GetProperty("remedy");
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("pendingReason").GetString()).ShouldBe(("AwaitingDecision", "REPAIR_FAILED_LIMIT"));
        Strings(remedy.GetProperty("allowedRemedies")).ShouldBe(["Replacement"]);
        var decided = await PostAsync(
            reviewer, $"/api/claims/{X9}/remedy-decisions", remedy.GetProperty("etag").GetString(),
            new JsonObject { ["remedy"] = "Replacement", ["reason"] = DecisionReason });
        decided.Status.ShouldBe(HttpStatusCode.Created, decided.Body);

        // The replacement starts awaiting a unit with the return recorded by the system, referring to the repair hand-over.
        var replacement = await ReplacementAsync(agent, X9);
        (replacement.GetProperty("status").GetString(), replacement.GetProperty("returnRule").GetString(), replacement.GetProperty("returnState").GetString())
            .ShouldBe(("AwaitingUnit", "AtCollection", "HeldByRepairCentre"));
        var faulty = replacement.GetProperty("faultyProductReturn");
        (faulty.GetProperty("returnedOn").GetString(), faulty.GetProperty("heldByRepairCentre").GetBoolean(), faulty.GetProperty("serialMatches").GetBoolean())
            .ShouldBe((Today(), true, true));
        faulty.TryGetProperty("serialSeen", out _).ShouldBeFalse();
        var allowed = Strings(replacement.GetProperty("allowedEvents"));
        allowed.ShouldContain("UnitReserved");
        allowed.ShouldNotContain("ReturnRecorded");
        var systemReturn = replacement.GetProperty("events").EnumerateArray().Last();
        (systemReturn.GetProperty("type").GetString(), systemReturn.GetProperty("source").GetString(), systemReturn.GetProperty("actor").GetString())
            .ShouldBe(("ReturnRecorded", "System", "system"));
        (systemReturn.GetProperty("fromStatus").GetString(), systemReturn.GetProperty("toStatus").GetString()).ShouldBe(("AwaitingUnit", "AwaitingUnit"));

        (await ReturnRowAsync(X9)).ShouldBe(new ReturnRow(DateOnly.FromDateTime(DateTime.UtcNow), null, null, true, handoverId, null, "system"));
        var events = await EventRowsAsync(X9);
        events.Select(e => (e.Seq, e.Type, e.Source, e.Actor)).ShouldBe(
            [(1, "LocationSuggested", "System", "system"), (2, "ReturnRecorded", "System", "system")]);
        events[1].Payload.ShouldContain(handoverId.ToString());
        (await TrailAsync(X9)).Where(e => e.Step == "FaultyProductReturned").ShouldHaveSingleItem().Payload.ShouldContain(handoverId.ToString());
        (await ReplacementRowAsync(X9)).ServiceCenterId.ShouldBe(await FirstReplacementLocationAsync(Aurora, "NA"));

        // The customer is not asked to bring the product.
        var reference = await ReferenceAsync(X9);
        var contact = await ContactAsync(X9);
        var waiting = (await ClaimantViewAsync(WarrantyAppFixture.AuroraHost, reference, contact)).GetProperty("remedy");
        waiting.GetProperty("label").GetString().ShouldBe(PreparingLabel);
        Steps(waiting).ShouldBe([("Product returned", "done"), ("Ready for collection", "current"), ("Handed to you", "upcoming")]);

        // Under AtCollection the hand-over needs no return confirmation: the product is already with the company.
        var serial = NewUnitSerial("AT10");
        await RecordAsync(agent, X9, Reserve(serial, DateTimeOffset.UtcNow));
        var handedOver = await RecordAsync(agent, X9, HandOver(returnConfirmed: null, DateTimeOffset.UtcNow));
        (handedOver.GetProperty("status").GetString(), handedOver.GetProperty("returnState").GetString())
            .ShouldBe(("HandedToCustomer", "HeldByRepairCentre"));
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", X9)).ShouldBe("Completed");
        (await ScalarAsync<string>("select original_serial from catalog.replacement_serials where serial_number = @value", serial))
            .ShouldBe(await ClaimSerialAsync(X9));

        var claimant = await ClaimantViewAsync(WarrantyAppFixture.AuroraHost, reference, contact);
        claimant.GetProperty("remedy").GetProperty("label").GetString().ShouldBe("Replacement handed over");
        Steps(claimant.GetProperty("remedy")).ShouldBe([("Product returned", "done"), ("Ready for collection", "done"), ("Handed to you", "done")]);
        ShouldShowNoStaffData(claimant, agent, serial, "JOB-0309");
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    /// <summary>
    /// A new claim like X6: a copy of the seeded X6 claim row with its own ID, reference, serial and email (submitted
    /// through the agent portal by <paramref name="submittedBy"/> when given), the remedy Replacement decided by a reviewer
    /// on X6's policy version, and a replacement with X6's rules, status (<c>AwaitingReturn</c>) and suggested location
    /// (its <c>LocationSuggested</c> event included), as the executor creates it.
    /// </summary>
    private async Task<(Guid ClaimId, string Reference)> CopyOfX6Async(TestStaffUser? submittedBy = null)
    {
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
            command.Parameters.AddWithValue("source", X6);
            command.Parameters.AddWithValue("id", claimId);
            command.Parameters.AddWithValue("reference", reference);
            command.Parameters.AddWithValue("serial", $"BS11-C-{claimId:N}".ToUpperInvariant());
            command.Parameters.AddWithValue("email", CopyContact);
            command.Parameters.AddWithValue("channel", WireName.Of(ClaimChannel.AgentPortal));
            command.Parameters.AddWithValue("submittedBy", submittedBy?.Subject ?? string.Empty);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            command.Transaction = transaction;
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
            await transaction.CommitAsync(Ct);
        }

        var source = await ReplacementRowAsync(X6);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        using (TenantContextScope.Begin(Borealis, "borealis"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedy = ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), Borealis, claimId, source.RemedyPolicyVersionId, Enum.Parse<RemedyScenario>(source.RemedyScenario),
                source.AllowedRemedies.Select(Enum.Parse<RemedyType>), RemedyPendingReason.RepairNotEconomical, at);
            remedy.Decide(RemedyType.Replacement, at);
            scope.ServiceProvider.GetRequiredService<IRemedyRepository>().Add(remedy);
            scope.ServiceProvider.GetRequiredService<IReplacementRepository>().Add(Replacement.Create(
                Guid.CreateVersion7(), Borealis, claimId, source.PolicyVersionId, Enum.Parse<ReplacementProductRule>(source.ProductRule),
                Enum.Parse<ReplacementReturnRule>(source.ReturnRule), source.MinimumCoverMonths, Enum.Parse<ReplacementStatus>(source.Status),
                source.ServiceCenterId, at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        if (submittedBy is not null)
        {
            (await ScalarAsync<string>("select submitted_by from claims.claims where id = @value", claimId)).ShouldBe(submittedBy.Subject);
        }

        return (claimId, reference);
    }

    private static JsonObject ReturnRecorded(string serialSeen, string? note = null) => Event("ReturnRecorded", body =>
    {
        body["returnedOn"] = Today();
        body["serialSeen"] = serialSeen;
        body["source"] = "InPerson";
        if (note is not null)
        {
            body["note"] = note;
        }
    });

    private static JsonObject Accept(string? reason) => Event("ReturnAccepted", body =>
    {
        if (reason is not null)
        {
            body["reason"] = reason;
        }
    });

    private static JsonObject Reserve(string serial, DateTimeOffset? reportedAt = null) => Event("UnitReserved", body =>
    {
        body["serialNumber"] = serial;
        if (reportedAt is { } at)
        {
            body["reportedAt"] = at;
        }
    });

    private static JsonObject HandOver(bool? returnConfirmed, DateTimeOffset? reportedAt = null) => Event("HandedToCustomer", body =>
    {
        body["source"] = "InPerson";
        body["handedOverOn"] = Today();
        if (returnConfirmed is { } confirmed)
        {
            body["returnConfirmed"] = confirmed;
        }

        if (reportedAt is { } at)
        {
            body["reportedAt"] = at;
        }
    });

    /// <summary>A replacement or repair event request reported by phone a minute ago, adjusted by <paramref name="adjust"/>.</summary>
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

    /// <summary>FR-034: the claimant view never shows serials, notes, reasons, staff identities or return checks.</summary>
    private static void ShouldShowNoStaffData(JsonElement claimant, TestStaffUser staff, params string[] hidden)
    {
        var text = claimant.GetRawText();
        foreach (var value in hidden)
        {
            text.ShouldNotContain(value, Case.Insensitive);
        }

        text.ShouldNotContain(staff.Subject);
        text.ShouldNotContain(staff.Username);
        text.ShouldNotContain("serialNumber", Case.Insensitive);
        text.ShouldNotContain("serialSeen", Case.Insensitive);
        text.ShouldNotContain("serialMatches", Case.Insensitive);
        text.ShouldNotContain("returnState", Case.Insensitive);
        text.ShouldNotContain("Mismatch", Case.Insensitive);
    }

    private static List<(string Title, string State)> Steps(JsonElement remedy)
        => remedy.GetProperty("replacementSteps").EnumerateArray()
            .Select(s => (s.GetProperty("title").GetString()!, s.GetProperty("state").GetString()!))
            .ToList();

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

    private Task<PostResponse> EventAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
        => PostAsync(user, $"/api/claims/{claimId}/replacement/events", ifMatch, body);

    /// <summary>Records <paramref name="body"/> with the replacement's current ETag and returns the updated replacement (201).</summary>
    private async Task<JsonElement> RecordAsync(TestStaffUser user, Guid claimId, JsonObject body)
    {
        var response = await EventAsync(user, claimId, ETagOf(await ReplacementAsync(user, claimId)), body);
        response.Status.ShouldBe(HttpStatusCode.Created, $"{body["type"]}: {response.Body}");
        return JsonSerializer.Deserialize<JsonElement>(response.Body);
    }

    /// <summary>Records the 002 repair event <paramref name="body"/> on X9 with the repair's current ETag (201).</summary>
    private async Task<JsonElement> RecordRepairAsync(TestStaffUser user, JsonObject body)
    {
        var etag = (await DetailAsync(user, X9)).GetProperty("repair").GetProperty("etag").GetString();
        var response = await PostAsync(user, $"/api/claims/{X9}/repair/events", etag, body);
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

    /// <summary>The first active centre of <paramref name="tenant"/> in <paramref name="region"/> that issues replacements and serves tablets.</summary>
    private Task<Guid> FirstReplacementLocationAsync(Guid tenant, string region) => ScalarAsync<Guid>(
        $"""
        select id from integration.service_centers
        where tenant_id = @value and region = '{region}' and is_active and issues_replacements and 'tablet' = any(capabilities)
        order by priority, name, id limit 1
        """,
        tenant);

    private async Task<ReplacementRow> ReplacementRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select r.status, r.product_rule, r.return_rule, r.return_state, r.minimum_cover_months, r.policy_version_id,
                   l.service_center_id, l.assignment, m.policy_version_id, m.scenario, m.allowed_remedies
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
            reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetGuid(8), reader.GetString(9), reader.GetFieldValue<string[]>(10));
    }

    /// <summary>The claim's <c>claims.faulty_product_returns</c> row, or null when no return is recorded.</summary>
    private async Task<ReturnRow?> ReturnRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select f.returned_on, f.service_center_id, f.serial_seen, f.serial_matches, f.repair_handover_id, f.note, f.recorded_by
            from claims.faulty_product_returns f join claims.replacements r on r.id = f.replacement_id
            where r.claim_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        {
            return null;
        }

        return new ReturnRow(
            reader.GetFieldValue<DateOnly>(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6));
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

    /// <summary>Every queued customer notification of the claim, of any template.</summary>
    private Task<long> NotificationCountAsync(Guid claimId)
        => ScalarAsync<long>("select count(*) from integration.notifications where claim_id = @value", claimId);

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
        string? Assignment,
        Guid RemedyPolicyVersionId,
        string RemedyScenario,
        string[] AllowedRemedies);

    private sealed record ReturnRow(
        DateOnly ReturnedOn,
        Guid? ServiceCenterId,
        string? SerialSeen,
        bool SerialMatches,
        Guid? RepairHandoverId,
        string? Note,
        string RecordedBy);

    private sealed record EventRow(int Seq, string Type, string? FromStatus, string? ToStatus, string Source, string Actor, string Payload);

    private sealed record TrailRow(string Step, string Actor, string Payload);
}
