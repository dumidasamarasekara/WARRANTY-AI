using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Feature 003 user story 5 (quickstart X16, X17; FR-009, FR-010, FR-011, FR-017, FR-027, FR-031): staff move a replacement
/// to another eligible location, record a unit the customer refused and correct an entry recorded wrongly through
/// <c>POST /api/claims/{claimId}/replacement/events</c> (T356). A move leaves one active location and releases a unit
/// reserved at the old one; a refused unit stays in the history; every newly reserved unit gets its own ready notification;
/// the remedy never changes; a correction applies the corrected value while the original entry stays in the history and
/// queues no notification. A location of another company is not found (404 + <c>ACCESS_DENIED</c>).
/// <para>
/// Every test works on its own copy of the seeded X1 claim (US1's <c>CopyOfX1Async</c>: Aurora AUR-TAB10, Europe, awaiting a
/// unit at Amsterdam), so the tests do not depend on order. The Borealis location is read from the seeded X6 claim, which is
/// left unchanged.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class US5_MoveRefusalCorrectionTests(WarrantyAppFixture fixture)
{
    private const string Pending = "Pending T356";
    private const string ReadyTemplate = "ReplacementReadyForCollection";
    private const string MoveReason = "The customer moved to Hamburg and collects the replacement there.";
    private const string RefusalReason = "The customer refused the unit at collection: the screen is scratched.";
    private const string CorrectionReason = "The unit serial was typed in wrongly; the location confirmed the real one.";

    /// <summary>The contact of US1's copies of X1.</summary>
    private const string CopyContact = "replacement.collection@example.test";

    /// <summary>Aurora Service Center Amsterdam: X1's suggested location (Europe, priority 10).</summary>
    private static readonly Guid Amsterdam = Guid.Parse("11111111-0003-7000-8000-000000000002");

    /// <summary>Aurora Service Center Hamburg: the second Aurora replacement location of Europe.</summary>
    private static readonly Guid Hamburg = Guid.Parse("11111111-0003-7000-8000-000000000004");

    /// <summary>Seeded X6-borealis: awaiting the return at a Borealis location (only read here).</summary>
    private static readonly Guid X6 = Guid.Parse("22222222-0006-7000-8000-000000000006");

    private static readonly string Approved = WireName.Of(ClaimStatus.Approved);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- X16: move, reserve, refuse, reserve ---------------------------------------------------------

    [Fact(Skip = Pending)]
    public async Task X16_a_move_and_a_refused_unit_keep_one_active_location_a_notification_per_unit_and_the_remedy()
    {
        var agent = TestStaffUsers.AgentAurora;
        var (claimId, reference) = await CopyOfX1Async();
        var decisionsBefore = await ScalarAsync<long>("select count(*) from claims.remedy_decisions where claim_id = @value", claimId);
        var first = NewUnitSerial();
        await RecordAsync(agent, claimId, Reserve(first));

        // Move from Ready for collection: Hamburg becomes the only active location, the Amsterdam unit is released.
        var moved = await RecordAsync(agent, claimId, Event("Moved", body =>
        {
            body["locationId"] = Hamburg;
            body["reason"] = MoveReason;
        }));
        moved.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        moved.GetProperty("activeLocation").GetProperty("name").GetString().ShouldBe("Aurora Service Center Hamburg");
        moved.GetProperty("currentUnit").GetProperty("result").GetString().ShouldBe("Released");
        Strings(moved.GetProperty("allowedEvents")).ShouldContain("UnitReserved");
        Strings(moved.GetProperty("allowedEvents")).ShouldNotContain("HandedToCustomer");

        // The claimant sees the new location and that the replacement is being prepared, never the reason.
        var claimant = (await ClaimantViewAsync(reference)).GetProperty("remedy");
        claimant.GetProperty("label").GetString().ShouldBe("We are preparing your replacement");
        claimant.GetProperty("location").GetProperty("name").GetString().ShouldBe("Aurora Service Center Hamburg");
        claimant.GetRawText().ShouldNotContain(MoveReason);

        var second = NewUnitSerial();
        await RecordAsync(agent, claimId, Reserve(second));
        var refused = await RecordAsync(agent, claimId, Event("UnitRefused", body =>
        {
            body["reason"] = RefusalReason;
            body["source"] = "InPerson";
        }));
        refused.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        refused.GetProperty("currentUnit").GetProperty("result").GetString().ShouldBe("Refused");

        var third = NewUnitSerial();
        var ready = await RecordAsync(agent, claimId, Reserve(third));
        ready.GetProperty("status").GetString().ShouldBe("ReadyForCollection");
        ready.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ShouldBe(
            ["LocationSuggested", "UnitReserved", "Moved", "UnitReserved", "UnitRefused", "UnitReserved"]);

        // One active location; the old one and its unit stay in the history.
        (await LocationsAsync(claimId)).ShouldBe(
        [
            (Amsterdam, "Suggested", false),
            (Hamburg, "Moved", true),
        ]);
        var units = await UnitsAsync(claimId);
        units.Select(u => (u.Seq, u.ServiceCenterId, u.SerialNumber, u.Result)).ShouldBe(
        [
            (1, Amsterdam, first, "Released"),
            (2, Hamburg, second, "Refused"),
            (3, Hamburg, third, "Reserved"),
        ]);
        units[1].ResultReason.ShouldBe(RefusalReason);

        // A notification per reserved unit: three rows with distinct keys.
        (await NotificationsAsync(claimId)).ShouldBe(units.Select(u => $"replacement-ready:{u.Id}").ToList(), ignoreOrder: true);

        // FR-027: neither the move nor the refusal changed the remedy or the claim.
        (await ScalarAsync<string>("select remedy from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("Replacement");
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("InProgress");
        (await ScalarAsync<long>("select count(*) from claims.remedy_decisions where claim_id = @value", claimId)).ShouldBe(decisionsBefore);
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId)).ShouldBe(Approved);
        (await TrailStepsAsync(claimId)).ShouldContain("ReplacementLocationAssigned");
    }

    [Fact(Skip = Pending)]
    public async Task A_refusal_while_awaiting_a_unit_conflicts_naming_the_status()
    {
        var (claimId, _) = await CopyOfX1Async();

        var response = await PostAsync(
            TestStaffUsers.AgentAurora, claimId, await ETagAsync(TestStaffUsers.AgentAurora, claimId),
            Event("UnitRefused", body => body["reason"] = RefusalReason));

        response.Status.ShouldBe(HttpStatusCode.Conflict, response.Body);
        response.Body.ShouldContain("AwaitingUnit");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested"]);
    }

    [Fact(Skip = Pending)]
    public async Task An_auditor_cannot_move_a_replacement()
    {
        var (claimId, _) = await CopyOfX1Async();

        var response = await PostAsync(
            TestStaffUsers.AuditorAurora, claimId, await ETagAsync(TestStaffUsers.AgentAurora, claimId),
            Event("Moved", body =>
            {
                body["locationId"] = Hamburg;
                body["reason"] = MoveReason;
            }));

        response.Status.ShouldBe(HttpStatusCode.Forbidden, response.Body);
        (await LocationsAsync(claimId)).ShouldBe([(Amsterdam, "Suggested", true)]);
    }

    // ---- X17: corrections --------------------------------------------------------------------------

    [Fact(Skip = Pending)]
    public async Task X17_a_corrected_unit_serial_applies_and_the_original_entry_stays_in_the_history()
    {
        var agent = TestStaffUsers.AgentAurora;
        var (claimId, reference) = await CopyOfX1Async();
        var mistyped = NewUnitSerial();
        var reserved = await RecordAsync(agent, claimId, Reserve(mistyped));
        var reservedSeq = reserved.GetProperty("events").EnumerateArray().Last().GetProperty("seq").GetInt32();
        var corrected = NewUnitSerial();

        var view = await RecordAsync(agent, claimId, Event("Correction", body =>
        {
            body["correctsSeq"] = reservedSeq;
            body["serialNumber"] = corrected.ToLowerInvariant();
            body["reason"] = CorrectionReason;
            body["source"] = "Email";
        }));

        view.GetProperty("status").GetString().ShouldBe("ReadyForCollection");
        view.GetProperty("currentUnit").GetProperty("serialNumber").GetString().ShouldBe(corrected);
        var events = view.GetProperty("events").EnumerateArray().ToList();
        events.Select(e => e.GetProperty("type").GetString()).ShouldBe(["LocationSuggested", "UnitReserved", "Correction"]);
        events[^1].GetProperty("correctsSeq").GetInt32().ShouldBe(reservedSeq);
        events[1].TryGetProperty("correctsSeq", out _).ShouldBeFalse();

        // The original entry is unchanged in the append-only events; the correction references it; the unit is the same one.
        var original = await EventRowAsync(claimId, reservedSeq);
        original.Payload.ShouldContain(mistyped);
        (await ScalarAsync<long>(
            "select corrects_event_id from audit.replacement_events where claim_id = @value and type = 'Correction'", claimId))
            .ShouldBe(original.Id);
        var unit = (await UnitsAsync(claimId)).ShouldHaveSingleItem();
        (unit.SerialNumber, unit.Result).ShouldBe((corrected, "Reserved"));

        // No second notification for the same unit; the claimant still sees no serial.
        (await NotificationsAsync(claimId)).ShouldHaveSingleItem().ShouldBe($"replacement-ready:{unit.Id}");
        var claimant = await ClaimantViewAsync(reference);
        claimant.GetRawText().ShouldNotContain(corrected, Case.Insensitive);
        claimant.GetRawText().ShouldNotContain(mistyped, Case.Insensitive);
        claimant.GetProperty("remedy").GetProperty("label").GetString().ShouldBe("Your replacement is ready for collection");
    }

    [Fact(Skip = Pending)]
    public async Task X17_a_reservation_corrected_back_to_awaiting_a_unit_and_reserved_again_queues_no_second_notification()
    {
        var agent = TestStaffUsers.AgentAurora;
        var (claimId, _) = await CopyOfX1Async();
        var serial = NewUnitSerial();
        var reserved = await RecordAsync(agent, claimId, Reserve(serial));
        var reservedSeq = reserved.GetProperty("events").EnumerateArray().Last().GetProperty("seq").GetInt32();

        var sentBack = await RecordAsync(agent, claimId, Event("Correction", body =>
        {
            body["correctsSeq"] = reservedSeq;
            body["toStatus"] = "AwaitingUnit";
            body["reason"] = "The location had not reserved the unit yet; it was recorded too early.";
        }));
        sentBack.GetProperty("status").GetString().ShouldBe("AwaitingUnit");

        var again = await RecordAsync(agent, claimId, Reserve(serial));

        again.GetProperty("status").GetString().ShouldBe("ReadyForCollection");
        var unit = (await UnitsAsync(claimId)).ShouldHaveSingleItem();
        (await NotificationsAsync(claimId)).ShouldHaveSingleItem().ShouldBe($"replacement-ready:{unit.Id}");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "UnitReserved", "Correction", "UnitReserved"]);
    }

    [Fact(Skip = Pending)]
    public async Task A_correction_without_a_reason_or_naming_the_claims_own_serial_is_refused()
    {
        var agent = TestStaffUsers.AgentAurora;
        var (claimId, _) = await CopyOfX1Async();
        var serial = NewUnitSerial();
        var reserved = await RecordAsync(agent, claimId, Reserve(serial));
        var reservedSeq = reserved.GetProperty("events").EnumerateArray().Last().GetProperty("seq").GetInt32();
        var claimSerial = await ScalarAsync<string>("select serial_number from claims.claims where id = @value", claimId);
        var etag = ETagOf(reserved);

        var noReason = await PostAsync(agent, claimId, etag, Event("Correction", body =>
        {
            body["correctsSeq"] = reservedSeq;
            body["serialNumber"] = NewUnitSerial();
        }));
        var claimsOwn = await PostAsync(agent, claimId, etag, Event("Correction", body =>
        {
            body["correctsSeq"] = reservedSeq;
            body["serialNumber"] = claimSerial;
            body["reason"] = CorrectionReason;
        }));

        noReason.Status.ShouldBe(HttpStatusCode.BadRequest, noReason.Body);
        noReason.Body.ShouldContain("reason");
        claimsOwn.Status.ShouldBe(HttpStatusCode.BadRequest, claimsOwn.Body);
        claimsOwn.Body.ShouldContain("serialNumber");
        (await UnitsAsync(claimId)).ShouldHaveSingleItem().SerialNumber.ShouldBe(serial);
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "UnitReserved"]);
    }

    // ---- FR-011: a location of another company -----------------------------------------------------

    [Fact(Skip = Pending)]
    public async Task A_move_of_an_Aurora_replacement_to_a_Borealis_location_gets_404_with_ACCESS_DENIED_and_changes_nothing()
    {
        var agent = TestStaffUsers.AgentAurora;
        var borealisLocation = await ScalarAsync<Guid>(
            """
            select l.service_center_id from claims.replacements r
            join claims.replacement_locations l on l.id = r.active_location_id where r.claim_id = @value
            """,
            X6);
        var (claimId, _) = await CopyOfX1Async();
        var ready = await RecordAsync(agent, claimId, Reserve(NewUnitSerial()));
        var deniedBefore = await AccessDeniedCountAsync(borealisLocation);

        var response = await PostAsync(agent, claimId, ETagOf(ready), Event("Moved", body =>
        {
            body["locationId"] = borealisLocation;
            body["reason"] = "The customer moved to Lyon for work.";
        }));

        response.Status.ShouldBe(HttpStatusCode.NotFound, response.Body);
        response.Body.ShouldNotContain("Borealis");
        response.Body.ShouldNotContain("Lyon");
        (await AccessDeniedCountAsync(borealisLocation)).ShouldBe(deniedBefore + 1);
        (await LocationsAsync(claimId)).ShouldBe([(Amsterdam, "Suggested", true)]);
        (await UnitsAsync(claimId)).ShouldHaveSingleItem().Result.ShouldBe("Reserved");
        (await EventTypesAsync(claimId)).ShouldBe(["LocationSuggested", "UnitReserved"]);
        ETagOf(await ReplacementAsync(agent, claimId)).ShouldBe(ETagOf(ready));
    }

    // ---- Arrangements and requests -----------------------------------------------------------------

    private Task<(Guid ClaimId, string Reference)> CopyOfX1Async() => new US1_ReplacementCollectionTests(fixture).CopyOfX1Async();

    private static JsonObject Reserve(string serial) => Event("UnitReserved", body => body["serialNumber"] = serial);

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

    private static string ETagOf(JsonElement replacement) => replacement.GetProperty("etag").GetString().ShouldNotBeNull();

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    /// <summary>The claim detail's <c>replacement</c> (ReplacementView).</summary>
    private async Task<JsonElement> ReplacementAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("replacement");
    }

    private async Task<string> ETagAsync(TestStaffUser user, Guid claimId) => ETagOf(await ReplacementAsync(user, claimId));

    /// <summary>Records <paramref name="body"/> with the current ETag and returns the updated ReplacementView (201).</summary>
    private async Task<JsonElement> RecordAsync(TestStaffUser user, Guid claimId, JsonObject body)
    {
        var response = await PostAsync(user, claimId, await ETagAsync(user, claimId), body);
        response.Status.ShouldBe(HttpStatusCode.Created, response.Body);
        return JsonSerializer.Deserialize<JsonElement>(response.Body);
    }

    private async Task<PostResponse> PostAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
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
        return new PostResponse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The claimant view on the Aurora channel, opened with the reference and the copies' contact.</summary>
    private async Task<JsonElement> ClaimantViewAsync(string reference)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = CopyContact }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // ---- Database reads (as the owner, for assertions only) ----------------------------------------

    /// <summary>The replacement's locations in the order they were assigned: centre, assignment, active.</summary>
    private async Task<List<(Guid ServiceCenterId, string Assignment, bool IsActive)>> LocationsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select l.service_center_id, l.assignment, l.is_active
            from claims.replacement_locations l join claims.replacements r on r.id = l.replacement_id
            where r.claim_id = @id order by l.recorded_at, l.id
            """,
            connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(Guid, string, bool)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return rows;
    }

    private async Task<List<UnitRow>> UnitsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select u.id, u.seq, u.service_center_id, u.serial_number, u.result, u.result_reason
            from claims.replacement_units u join claims.replacements r on r.id = u.replacement_id
            where r.claim_id = @id order by u.seq
            """,
            connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<UnitRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new UnitRow(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private async Task<(long Id, string Payload)> EventRowAsync(Guid claimId, int seq)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select id, payload::text from audit.replacement_events where claim_id = @id and seq = @seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("seq", seq);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no replacement event {seq}");
        return (reader.GetInt64(0), reader.GetString(1));
    }

    private Task<List<string>> EventTypesAsync(Guid claimId)
        => ListAsync<string>("select type from audit.replacement_events where claim_id = @value order by seq", claimId);

    private Task<List<string>> TrailStepsAsync(Guid claimId)
        => ListAsync<string>("select step from audit.decision_trail_entries where claim_id = @value order by seq", claimId);

    /// <summary>The dedupe keys of the claim's ready notifications.</summary>
    private Task<List<string>> NotificationsAsync(Guid claimId)
        => ListAsync<string>(
            $"select coalesce(dedupe_key, '') from integration.notifications where claim_id = @value and template = '{ReadyTemplate}'", claimId);

    /// <summary>Tenant-visible <c>ACCESS_DENIED</c> events whose target names <paramref name="id"/>.</summary>
    private Task<long> AccessDeniedCountAsync(Guid id)
        => ScalarAsync<long>(
            $"select count(*) from audit.security_events where kind = '{WireName.Of(SecurityEventKind.AccessDenied)}' "
            + "and target like '%' || @value || '%'",
            id.ToString());

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

    private sealed record PostResponse(HttpStatusCode Status, string Body);

    private sealed record UnitRow(Guid Id, int Seq, Guid ServiceCenterId, string SerialNumber, string Result, string? ResultReason);
}
