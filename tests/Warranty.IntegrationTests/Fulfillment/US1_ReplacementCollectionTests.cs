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
/// Feature 003 user story 1 (quickstart X1 – X5) and tenant isolation (X18, FR-011): staff record what happens at the
/// replacement location through <c>POST /api/claims/{claimId}/replacement/events</c> (T329) with the replacement ETag of
/// the claim detail (T328); the claimant sees the progress without serial numbers or staff-only data (FR-034).
/// <para>
/// X2 – X4 change the replacement, so each test uses its own copy of the seeded X1 claim (<c>RPMTX1A001</c>, Aurora
/// AUR-TAB10, Europe): the claim row with its own ID, reference, serial and email, and through the Application ports the
/// remedy Replacement decided by a reviewer and a replacement with X1's rules, status and suggested location. The seeded
/// X1, X5 and X6-borealis claims only get reads and requests that must be refused, so the tests do not depend on order.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class US1_ReplacementCollectionTests(WarrantyAppFixture fixture)
{
    private const string ReadyTemplate = "ReplacementReadyForCollection";
    private const string StaffNote = "Unit taken from the back-office stock; staff-only note.";
    private const string CopyContact = "replacement.collection@example.test";

    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;

    /// <summary>Seeded X1-aurora: AUR-TAB10, Europe, returned at collection; awaiting a unit at the suggested location.</summary>
    private static readonly Guid X1 = Guid.Parse("11111111-0006-7000-8000-000000000001");

    /// <summary>Seeded X5: AUR-TAB10, North America; awaiting a unit at Springfield (the refused hand-over).</summary>
    private static readonly Guid X5 = Guid.Parse("11111111-0006-7000-8000-000000000005");

    /// <summary>Seeded X6-borealis: BOR-SLATE11, Europe, returned before reservation; awaiting the return at Lyon.</summary>
    private static readonly Guid X6 = Guid.Parse("22222222-0006-7000-8000-000000000006");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- X1: the seeded replacement -----------------------------------------------------------------

    [Fact]
    public async Task X1_the_seeded_Aurora_claim_awaits_a_unit_at_the_suggested_location_and_the_claimant_sees_it()
    {
        var expected = await FirstReplacementLocationAsync(Aurora, "EU");
        var expectedName = await ScalarAsync<string>("select name from integration.service_centers where id = @value", expected);
        expectedName.ShouldBe("Aurora Service Center Amsterdam");

        var row = await ReplacementRowAsync(X1);
        (row.Status, row.ProductRule, row.ReturnRule, row.ReturnState).ShouldBe(
            ("AwaitingUnit", "SameCategoryAtLeastClaimValue", "AtCollection", "NotYetReturned"));
        (row.ServiceCenterId, row.Assignment).ShouldBe((expected, "Suggested"));
        (await EventTypesAsync(X1)).ShouldBe(["LocationSuggested"]);
        (await ScalarAsync<string>("select remedy from claims.claim_remedies where claim_id = @value", X1)).ShouldBe("Replacement");
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", X1)).ShouldBe("InProgress");

        var reference = await ScalarAsync<string>("select reference from claims.claims where id = @value", X1);
        var contact = await ScalarAsync<string>("select contact_email from claims.claims where id = @value", X1);
        var claimant = await ClaimantViewAsync(reference, contact);
        var remedy = claimant.GetProperty("remedy");
        remedy.GetProperty("remedy").GetString().ShouldBe("Replacement");
        remedy.GetProperty("label").GetString().ShouldBe("We are preparing your replacement");
        remedy.GetProperty("completed").GetBoolean().ShouldBeFalse();
        remedy.GetProperty("location").GetProperty("name").GetString().ShouldBe(expectedName);
        remedy.GetProperty("location").TryGetProperty("contact", out _).ShouldBeFalse();
        remedy.TryGetProperty("replacementModelName", out _).ShouldBeFalse();
        Steps(remedy).ShouldBe([("Ready for collection", "current"), ("Handed to you", "upcoming")]);
        claimant.GetRawText().ShouldNotContain(await ClaimSerialAsync(X1), Case.Insensitive);

        // The copies X2 – X4 work on are X1 with their own identity: same status and location, same claimant view.
        var (copyId, copyReference) = await CopyOfX1Async();
        var copy = await ReplacementRowAsync(copyId);
        (copy.Status, copy.ProductRule, copy.ReturnRule, copy.ServiceCenterId).ShouldBe(
            (row.Status, row.ProductRule, row.ReturnRule, row.ServiceCenterId));
        var copyRemedy = (await ClaimantViewAsync(copyReference)).GetProperty("remedy");
        copyRemedy.GetProperty("label").GetString().ShouldBe("We are preparing your replacement");
        copyRemedy.GetProperty("location").GetProperty("name").GetString().ShouldBe(expectedName);
    }

    [Fact]
    public async Task X1_staff_see_the_replacement_in_the_claim_detail_with_Reserve_unit_allowed_and_an_ETag()
    {
        var replacement = await ReplacementAsync(TestStaffUsers.AgentAurora, X1);

        replacement.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        replacement.GetProperty("returnRule").GetString().ShouldBe("AtCollection");
        replacement.GetProperty("returnState").GetString().ShouldBe("NotYetReturned");
        replacement.GetProperty("activeLocation").GetProperty("name").GetString().ShouldBe("Aurora Service Center Amsterdam");
        Strings(replacement.GetProperty("allowedEvents")).ShouldContain("UnitReserved");
        Strings(replacement.GetProperty("allowedEvents")).ShouldNotContain("HandedToCustomer");
        replacement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString())
            .ShouldBe(["LocationSuggested"]);
        ETagOf(replacement).ShouldNotBeNullOrWhiteSpace();

        // An auditor reads it but may record nothing.
        Strings((await ReplacementAsync(TestStaffUsers.AuditorAurora, X1)).GetProperty("allowedEvents")).ShouldBeEmpty();
    }

    // ---- X2: reserving a unit -------------------------------------------------------------------------

    [Fact]
    public async Task X2_reserving_a_unit_makes_it_ready_with_one_notification_and_the_claimant_sees_the_model_but_no_serial()
    {
        var (claimId, reference) = await CopyOfX1Async();
        var before = await ReplacementAsync(TestStaffUsers.AgentAurora, claimId);
        var serial = NewUnitSerial();
        var reportedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var response = await EventAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(before), Reserve(serial, reportedAt));

        response.Status.ShouldBe(HttpStatusCode.Created, response.Body);
        var replacement = JsonSerializer.Deserialize<JsonElement>(response.Body);
        replacement.GetProperty("status").GetString().ShouldBe("ReadyForCollection");
        replacement.GetProperty("etag").GetString().ShouldNotBe(ETagOf(before));
        var unit = replacement.GetProperty("currentUnit");
        (unit.GetProperty("seq").GetInt32(), unit.GetProperty("modelCode").GetString(), unit.GetProperty("serialNumber").GetString(),
            unit.GetProperty("result").GetString()).ShouldBe((1, "AUR-TAB10", serial, "Reserved"));
        Strings(replacement.GetProperty("allowedEvents")).ShouldContain("HandedToCustomer");
        var reserved = replacement.GetProperty("events").EnumerateArray().Last();
        (reserved.GetProperty("type").GetString(), reserved.GetProperty("source").GetString()).ShouldBe(("UnitReserved", "Phone"));
        (reserved.GetProperty("fromStatus").GetString(), reserved.GetProperty("toStatus").GetString())
            .ShouldBe(("AwaitingUnit", "ReadyForCollection"));
        reserved.GetProperty("reportedAt").GetDateTimeOffset().ShouldBe(reportedAt, TimeSpan.FromSeconds(1));

        // One status change, one reserved unit and one keyed notification; the trail names the agent.
        await ShouldBeReadyOnceAsync(claimId);
        (await TrailActorsAsync(claimId, "ReplacementStatusUpdated")).ShouldBe([TestStaffUsers.AgentAurora.Subject]);

        // The claimant sees ready and the model's name, never the serial or staff-only data. "Please bring your faulty
        // product with you" is the status page's copy for AtCollection (T333): the API shows no "Product returned" step.
        var modelName = await ScalarAsync<string>(
            "select p.name from catalog.products p join claims.claims c on c.product_id = p.id where c.id = @value", claimId);
        var claimant = await ClaimantViewAsync(reference);
        var remedy = claimant.GetProperty("remedy");
        remedy.GetProperty("label").GetString().ShouldBe("Your replacement is ready for collection");
        remedy.GetProperty("replacementModelName").GetString().ShouldBe(modelName);
        remedy.GetProperty("location").GetProperty("name").GetString().ShouldBe("Aurora Service Center Amsterdam");
        Steps(remedy).ShouldBe([("Ready for collection", "current"), ("Handed to you", "upcoming")]);
        ShouldShowNoStaffData(claimant, serial);
    }

    // ---- X3: one status change and one notification ---------------------------------------------------

    [Fact]
    public async Task X3_the_same_reservation_sent_again_with_the_old_ETag_gets_412_and_queues_no_second_notification()
    {
        var (claimId, _) = await CopyOfX1Async();
        var before = await ReplacementAsync(TestStaffUsers.AgentAurora, claimId);
        var serial = NewUnitSerial();

        var first = await EventAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(before), Reserve(serial));
        var repeated = await EventAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(before), Reserve(serial));

        first.Status.ShouldBe(HttpStatusCode.Created, first.Body);
        repeated.Status.ShouldBe(HttpStatusCode.PreconditionFailed, repeated.Body);
        await ShouldBeReadyOnceAsync(claimId);
    }

    [Fact]
    public async Task X3_two_staff_reserving_at_once_change_the_status_once_and_queue_one_notification()
    {
        var (claimId, _) = await CopyOfX1Async();
        var before = await ReplacementAsync(TestStaffUsers.AgentAurora, claimId);
        var serial = NewUnitSerial();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => EventAsync(
                i % 2 == 0 ? TestStaffUsers.AgentAurora : TestStaffUsers.ReviewerAurora, claimId, ETagOf(before), Reserve(serial))));

        responses.Count(r => r.Status == HttpStatusCode.Created).ShouldBe(1, string.Join("\n", responses.Select(r => $"{r.Status}: {r.Body}")));
        responses.Where(r => r.Status != HttpStatusCode.Created)
            .ShouldAllBe(r => r.Status == HttpStatusCode.PreconditionFailed || r.Status == HttpStatusCode.Conflict);
        await ShouldBeReadyOnceAsync(claimId);

        // A later repeat with the old ETag is refused as well.
        var late = await EventAsync(TestStaffUsers.ReviewerAurora, claimId, ETagOf(before), Reserve(serial));
        late.Status.ShouldBe(HttpStatusCode.PreconditionFailed, late.Body);
        await ShouldBeReadyOnceAsync(claimId);
    }

    // ---- X4: the hand-over to the customer -------------------------------------------------------------

    [Fact]
    public async Task X4_the_hand_over_needs_the_return_confirmed_then_completes_the_remedy_and_registers_the_serial()
    {
        var (claimId, reference) = await CopyOfX1Async();
        var serial = NewUnitSerial();
        await ReserveAsync(claimId, serial);
        var ready = await ReplacementAsync(TestStaffUsers.AgentAurora, claimId);

        var unconfirmed = await EventAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(ready), HandOver(returnConfirmed: null));

        unconfirmed.Status.ShouldBe(HttpStatusCode.BadRequest, unconfirmed.Body);
        unconfirmed.Body.ShouldContain("returnConfirmed");
        (await ReplacementRowAsync(claimId)).Status.ShouldBe("ReadyForCollection");
        (await ScalarAsync<long>("select count(*) from catalog.replacement_serials where claim_id = @value", claimId)).ShouldBe(0);

        var handedOver = await EventAsync(
            TestStaffUsers.AgentAurora, claimId, ETagOf(await ReplacementAsync(TestStaffUsers.AgentAurora, claimId)), HandOver(returnConfirmed: true));

        handedOver.Status.ShouldBe(HttpStatusCode.Created, handedOver.Body);
        var replacement = JsonSerializer.Deserialize<JsonElement>(handedOver.Body);
        replacement.GetProperty("status").GetString().ShouldBe("HandedToCustomer");
        replacement.GetProperty("returnState").GetString().ShouldBe("Returned");
        replacement.GetProperty("currentUnit").GetProperty("result").GetString().ShouldBe("HandedOver");
        replacement.GetProperty("allowedEvents").GetArrayLength().ShouldBe(0);

        var row = await ReplacementRowAsync(claimId);
        (row.Status, row.ReturnState).ShouldBe(("HandedToCustomer", "Returned"));
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("Completed");
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId)).ShouldBe(WireName.Of(ClaimStatus.Approved));

        // The handed-out serial is registered against the claim's serial and is in the catalog as the claimed model.
        var claimSerial = await ClaimSerialAsync(claimId);
        (await ScalarAsync<string>("select original_serial from catalog.replacement_serials where serial_number = @value", serial))
            .ShouldBe(claimSerial);
        (await ScalarAsync<Guid>("select claim_id from catalog.replacement_serials where serial_number = @value", serial)).ShouldBe(claimId);
        (await ScalarAsync<Guid>("select product_id from catalog.product_serials where serial_number = @value", serial))
            .ShouldBe(await ScalarAsync<Guid>("select product_id from claims.claims where id = @value", claimId));

        var claimant = await ClaimantViewAsync(reference);
        var remedy = claimant.GetProperty("remedy");
        (remedy.GetProperty("label").GetString(), remedy.GetProperty("completed").GetBoolean()).ShouldBe(("Replacement handed over", true));
        Steps(remedy).ShouldBe([("Ready for collection", "done"), ("Handed to you", "done")]);
        ShouldShowNoStaffData(claimant, serial);
    }

    [Fact]
    public async Task X4_reserving_the_claims_own_serial_or_a_serial_handed_out_on_another_claim_is_refused()
    {
        // A unit handed out on one copy ...
        var (handedOutClaim, _) = await CopyOfX1Async();
        var handedOut = NewUnitSerial();
        await ReserveAsync(handedOutClaim, handedOut);
        var handOver = await EventAsync(
            TestStaffUsers.AgentAurora, handedOutClaim, ETagOf(await ReplacementAsync(TestStaffUsers.AgentAurora, handedOutClaim)),
            HandOver(returnConfirmed: true));
        handOver.Status.ShouldBe(HttpStatusCode.Created, handOver.Body);

        // ... cannot be reserved on another, nor can that claim's own serial.
        var (claimId, _) = await CopyOfX1Async();
        var etag = ETagOf(await ReplacementAsync(TestStaffUsers.AgentAurora, claimId));
        foreach (var serial in new[] { await ClaimSerialAsync(claimId), handedOut })
        {
            var refused = await EventAsync(TestStaffUsers.AgentAurora, claimId, etag, Reserve(serial));
            refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.Body);
            refused.Body.ShouldContain("serialNumber");
        }

        (await ReplacementRowAsync(claimId)).Status.ShouldBe("AwaitingUnit");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested"]);
        (await NotificationsAsync(claimId)).ShouldBeEmpty();
        ETagOf(await ReplacementAsync(TestStaffUsers.AgentAurora, claimId)).ShouldBe(etag);
    }

    // ---- X5: an event the status does not allow ---------------------------------------------------------

    [Fact]
    public async Task X5_handing_over_before_a_unit_is_reserved_conflicts_naming_the_status_and_the_allowed_events()
    {
        var before = await ReplacementAsync(TestStaffUsers.AgentAurora, X5);
        before.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        var eventsBefore = await EventTypesAsync(X5);

        var response = await EventAsync(TestStaffUsers.AgentAurora, X5, ETagOf(before), HandOver(returnConfirmed: true));

        response.Status.ShouldBe(HttpStatusCode.Conflict, response.Body);
        response.MediaType.ShouldBe("application/problem+json");
        response.Body.ShouldContain("AwaitingUnit");
        response.Body.ShouldContain("UnitReserved");
        (await ReplacementRowAsync(X5)).Status.ShouldBe("AwaitingUnit");
        (await EventTypesAsync(X5)).ShouldBe(eventsBefore);
        (await NotificationsAsync(X5)).ShouldBeEmpty();
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", X5)).ShouldBe("InProgress");
    }

    // ---- X18: tenant isolation ---------------------------------------------------------------------------

    [Fact]
    public async Task X18_an_Aurora_agent_gets_404_for_the_replacement_locations_of_a_Borealis_claim()
    {
        var deniedBefore = await AccessDeniedCountAsync(X6);

        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        using var response = await client.GetAsync($"/api/claims/{X6}/replacement-locations", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        ShouldContainNoBorealisData(body);
        (await AccessDeniedCountAsync(X6)).ShouldBeGreaterThan(deniedBefore);
    }

    [Fact(Skip = "Pending T356")]
    public async Task X18_moving_an_Aurora_replacement_to_a_Borealis_location_gets_404_and_changes_nothing()
    {
        var borealisLocation = (await ReplacementRowAsync(X6)).ServiceCenterId.ShouldNotBeNull();
        var (claimId, _) = await CopyOfX1Async();
        var before = await ReplacementAsync(TestStaffUsers.AgentAurora, claimId);
        var locationsBefore = await ScalarAsync<long>(
            "select count(*) from claims.replacement_locations l join claims.replacements r on r.id = l.replacement_id where r.claim_id = @value",
            claimId);
        var deniedBefore = await AccessDeniedCountAsync(borealisLocation);

        var response = await EventAsync(
            TestStaffUsers.AgentAurora, claimId, ETagOf(before),
            Event("Moved", body => { body["locationId"] = borealisLocation; body["reason"] = "The customer moved to Lyon for work."; }));

        response.Status.ShouldBe(HttpStatusCode.NotFound, response.Body);
        ShouldContainNoBorealisData(response.Body);
        (await AccessDeniedCountAsync(borealisLocation)).ShouldBe(deniedBefore + 1);
        (await ScalarAsync<long>(
            "select count(*) from claims.replacement_locations l join claims.replacements r on r.id = l.replacement_id where r.claim_id = @value",
            claimId)).ShouldBe(locationsBefore);
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested"]);
        ETagOf(await ReplacementAsync(TestStaffUsers.AgentAurora, claimId)).ShouldBe(ETagOf(before));
    }

    [Fact]
    public async Task X18_an_Aurora_agent_recording_an_event_on_a_Borealis_claim_gets_404_and_changes_nothing()
    {
        var deniedBefore = await AccessDeniedCountAsync(X6);
        var eventsBefore = await EventTypesAsync(X6);
        var borealisETag = ETagOf(await ReplacementAsync(TestStaffUsers.AgentBorealis, X6));

        var response = await EventAsync(
            TestStaffUsers.AgentAurora, X6, borealisETag,
            Event("ReturnRecorded", body => { body["returnedOn"] = Today(); body["serialSeen"] = "BS11-00-0000"; }));

        response.Status.ShouldBe(HttpStatusCode.NotFound, response.Body);
        ShouldContainNoBorealisData(response.Body);
        (await AccessDeniedCountAsync(X6)).ShouldBeGreaterThan(deniedBefore);
        (await ReplacementRowAsync(X6)).Status.ShouldBe("AwaitingReturn");
        (await EventTypesAsync(X6)).ShouldBe(eventsBefore);
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    /// <summary>
    /// A new claim like X1: a copy of the seeded X1 claim row with its own ID, reference, serial and email, the remedy
    /// Replacement decided by a reviewer on X1's policy version, and a replacement with X1's rules and status at X1's
    /// suggested location (its <c>LocationSuggested</c> event included), as the executor creates it.
    /// </summary>
    private async Task<(Guid ClaimId, string Reference)> CopyOfX1Async()
    {
        var claimId = Guid.CreateVersion7();
        var reference = ClaimReference.Generate();
        await using (var connection = await OpenAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                create temp table claim_copy on commit drop as select * from claims.claims where id = @source;
                update claim_copy set id = @id, reference = @reference, serial_number = @serial, contact_email = @email;
                insert into claims.claims select * from claim_copy;
                """,
                connection);
            command.Parameters.AddWithValue("source", X1);
            command.Parameters.AddWithValue("id", claimId);
            command.Parameters.AddWithValue("reference", reference);
            command.Parameters.AddWithValue("serial", $"AT10-C-{claimId:N}".ToUpperInvariant());
            command.Parameters.AddWithValue("email", CopyContact);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            command.Transaction = transaction;
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
            await transaction.CommitAsync(Ct);
        }

        var source = await ReplacementRowAsync(X1);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedy = ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), Aurora, claimId, source.RemedyPolicyVersionId, Enum.Parse<RemedyScenario>(source.RemedyScenario),
                source.AllowedRemedies.Select(Enum.Parse<RemedyType>), RemedyPendingReason.RepairFailedLimit, at);
            remedy.Decide(RemedyType.Replacement, at);
            scope.ServiceProvider.GetRequiredService<IRemedyRepository>().Add(remedy);
            scope.ServiceProvider.GetRequiredService<IReplacementRepository>().Add(Replacement.Create(
                Guid.CreateVersion7(), Aurora, claimId, source.PolicyVersionId, Enum.Parse<ReplacementProductRule>(source.ProductRule),
                Enum.Parse<ReplacementReturnRule>(source.ReturnRule), source.MinimumCoverMonths, Enum.Parse<ReplacementStatus>(source.Status),
                source.ServiceCenterId, at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        return (claimId, reference);
    }

    /// <summary>Reserves a unit with <paramref name="serial"/> on the copy as <c>agent.aurora</c> (X2).</summary>
    private async Task ReserveAsync(Guid claimId, string serial)
    {
        var reserved = await EventAsync(
            TestStaffUsers.AgentAurora, claimId, ETagOf(await ReplacementAsync(TestStaffUsers.AgentAurora, claimId)), Reserve(serial));
        reserved.Status.ShouldBe(HttpStatusCode.Created, reserved.Body);
    }

    private static JsonObject Reserve(string serial, DateTimeOffset? reportedAt = null) => Event("UnitReserved", body =>
    {
        body["serialNumber"] = serial;
        body["note"] = StaffNote;
        if (reportedAt is { } at)
        {
            body["reportedAt"] = at;
        }
    });

    private static JsonObject HandOver(bool? returnConfirmed) => Event("HandedToCustomer", body =>
    {
        body["source"] = "InPerson";
        body["handedOverOn"] = Today();
        if (returnConfirmed is { } confirmed)
        {
            body["returnConfirmed"] = confirmed;
        }
    });

    /// <summary>A <c>ReplacementEventRequest</c> reported by phone a minute ago, adjusted by <paramref name="adjust"/>.</summary>
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

    /// <summary>A new AUR-TAB10 unit serial in the catalog's normalised form, unused on any claim.</summary>
    private static string NewUnitSerial() => $"AT10-R-{Guid.CreateVersion7():N}".ToUpperInvariant();

    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Exactly one reservation: status ready, one UnitReserved event, one unit and one notification keyed by it.</summary>
    private async Task ShouldBeReadyOnceAsync(Guid claimId)
    {
        (await ReplacementRowAsync(claimId)).Status.ShouldBe("ReadyForCollection");
        (await EventTypesAsync(claimId)).Count(t => t == "UnitReserved").ShouldBe(1);
        var unitId = (await UnitIdsAsync(claimId)).ShouldHaveSingleItem();
        (await NotificationsAsync(claimId)).ShouldHaveSingleItem().ShouldBe($"replacement-ready:{unitId}");
    }

    /// <summary>FR-034: the claimant view never shows the unit's serial, notes, staff identities or return checks.</summary>
    private static void ShouldShowNoStaffData(JsonElement claimant, string serial)
    {
        var text = claimant.GetRawText();
        text.ShouldNotContain(serial, Case.Insensitive);
        text.ShouldNotContain(StaffNote);
        text.ShouldNotContain(TestStaffUsers.AgentAurora.Subject);
        text.ShouldNotContain(TestStaffUsers.AgentAurora.Username);
        text.ShouldNotContain("serialNumber", Case.Insensitive);
        text.ShouldNotContain("returnState", Case.Insensitive);
    }

    private static void ShouldContainNoBorealisData(string body)
    {
        body.ShouldNotContain("Borealis");
        body.ShouldNotContain("Lyon");
    }

    private static List<(string Title, string State)> Steps(JsonElement remedy)
        => remedy.GetProperty("replacementSteps").EnumerateArray()
            .Select(s => (s.GetProperty("title").GetString()!, s.GetProperty("state").GetString()!))
            .ToList();

    private static string ETagOf(JsonElement replacement) => replacement.GetProperty("etag").GetString().ShouldNotBeNull();

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    // ---- API ---------------------------------------------------------------------------------------

    /// <summary>The claim detail's <c>replacement</c> (ReplacementView, T328).</summary>
    private async Task<JsonElement> ReplacementAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("replacement");
    }

    private async Task<PostResponse> EventAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/replacement/events")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await client.SendAsync(request, Ct);
        return new PostResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The claimant view on the Aurora channel, opened with the reference and <paramref name="contact"/> (default: a copy's).</summary>
    private async Task<JsonElement> ClaimantViewAsync(string reference, string contact = CopyContact)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token);
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

    private Task<List<string>> EventTypesAsync(Guid claimId)
        => ListAsync<string>("select type from audit.replacement_events where claim_id = @value order by seq", claimId);

    private Task<List<Guid>> UnitIdsAsync(Guid claimId)
        => ListAsync<Guid>(
            "select u.id from claims.replacement_units u join claims.replacements r on r.id = u.replacement_id where r.claim_id = @value order by u.seq",
            claimId);

    private Task<List<string>> TrailActorsAsync(Guid claimId, string step)
        => ListAsync<string>($"select actor from audit.decision_trail_entries where claim_id = @value and step = '{step}' order by seq", claimId);

    /// <summary>The dedupe keys of the claim's ready notifications.</summary>
    private Task<List<string>> NotificationsAsync(Guid claimId)
        => ListAsync<string>(
            $"select coalesce(dedupe_key, '') from integration.notifications where claim_id = @value and template = '{ReadyTemplate}'", claimId);

    private Task<string> ClaimSerialAsync(Guid claimId)
        => ScalarAsync<string>("select serial_number from claims.claims where id = @value", claimId);

    /// <summary>Tenant-visible <c>ACCESS_DENIED</c> events whose target names <paramref name="id"/>.</summary>
    private Task<long> AccessDeniedCountAsync(Guid id)
        => ScalarAsync<long>(
            $"select count(*) from audit.security_events where kind = '{WireName.Of(SecurityEventKind.AccessDenied)}' "
            + "and target like '%' || @value || '%'",
            id.ToString());

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
}
