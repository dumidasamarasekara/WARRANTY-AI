using System.Globalization;
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
/// Feature 002 user story 5 (quickstart R12, FR-022, FR-026, FR-027): staff transfer a repair to another eligible centre of
/// the tenant and correct an entry recorded wrongly, through <c>POST /api/claims/{claimId}/repair/events</c> (T256). A
/// transfer leaves one active centre and keeps the attempt count; a correction applies the corrected value while the
/// original entry stays in the history; a corrected ready mark never queues a second ready notification. A centre of
/// another tenant is not found (404 + <c>ACCESS_DENIED</c>).
/// <para>
/// R12 drives its own seeded claim (R12-aurora, AUR-TAB10, North America: Springfield, then Riverside). The refusals use
/// the seeded R1 claim, which they leave unchanged, so other tests can keep reading it.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "002")]
public sealed class US5_TransferCorrectionTests(WarrantyAppFixture fixture)
{
    private const string ReadyTemplate = "RepairReadyForCollection";
    private const string TransferReason = "Springfield cannot get the replacement panel for three weeks.";
    private const string CorrectionReason = "The hand-over date was typed in wrongly; the centre confirmed the real date.";
    private const string AuroraCustomer = "remedy.customer@aurora.example.com";

    /// <summary>Seeded R12-aurora: AUR-TAB10, North America; repair awaiting hand-over at Springfield.</summary>
    private static readonly Guid R12 = Guid.Parse("11111111-0005-7000-8000-000000000012");

    private const string R12Reference = "RMDYR12A01";

    /// <summary>Seeded R1: Aurora, North America; repair awaiting hand-over at Springfield (only refused requests here).</summary>
    private static readonly Guid R1 = Guid.Parse("11111111-0005-7000-8000-000000000001");

    private static readonly Guid Springfield = Guid.Parse("11111111-0003-7000-8000-000000000001");

    /// <summary>The second Aurora centre of North America.</summary>
    private static readonly Guid Riverside = Guid.Parse("11111111-0003-7000-8000-000000000003");

    /// <summary>Borealis Repair Hub Lyon: an active centre of the other tenant.</summary>
    private static readonly Guid BorealisLyon = Guid.Parse("22222222-0003-7000-8000-000000000003");

    private static readonly string Approved = WireName.Of(ClaimStatus.Approved);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- R12: transfer after one failure, then a hand-over date correction -----------------------------

    [Fact]
    public async Task R12_a_transfer_after_one_failure_leaves_one_active_centre_and_a_date_correction_keeps_the_original_entry()
    {
        var agent = TestStaffUsers.AgentAurora;
        (await RepairAsync(agent, R12)).GetProperty("status").GetString().ShouldBe("AwaitingHandover");
        var handedOver = await RecordAsync(agent, R12, HandOver(Today()));
        var handedOverSeq = handedOver.GetProperty("events").EnumerateArray().Last().GetProperty("seq").GetInt32();
        await RecordAsync(agent, R12, Event("RepairFailed", body => body["reason"] = "The display flickers again after the first repair."));
        await RecordAsync(agent, R12, Event("NewAttemptStarted", body => body["productLocation"] = "AtCentre"));

        // Transfer to the second centre of the region.
        var transferred = await RecordAsync(agent, R12, Event("Transferred", body =>
        {
            body["repairCentreId"] = Riverside;
            body["reason"] = TransferReason;
        }));

        (transferred.GetProperty("status").GetString(), transferred.GetProperty("failedAttempts").GetInt32()).ShouldBe(("AwaitingHandover", 1));
        transferred.GetProperty("activeCentre").GetProperty("id").GetGuid().ShouldBe(Riverside);
        transferred.TryGetProperty("handedOverOn", out _).ShouldBeFalse("the new centre has not received the product yet");
        transferred.GetProperty("events").EnumerateArray().Last().GetProperty("type").GetString().ShouldBe("Transferred");
        var handovers = await HandoversAsync(R12);
        handovers.Count.ShouldBe(2);
        handovers.Single(h => h.IsActive).ServiceCenterId.ShouldBe(Riverside);
        handovers.Single(h => !h.IsActive).ShouldBe(new HandoverRow(Springfield, false, DateOnly.FromDateTime(DateTime.UtcNow)));
        (await AttemptsAsync(R12)).ShouldBe([new AttemptRow(1, "Failed", Springfield), new AttemptRow(2, "Open", Riverside)]);

        // Correct the date of the first hand-over: the corrected date applies, the original entry stays.
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var corrected = await RecordAsync(agent, R12, Event("Correction", body =>
        {
            body["correctsSeq"] = handedOverSeq;
            body["reason"] = CorrectionReason;
            body["handedOverOn"] = yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }));

        var events = corrected.GetProperty("events").EnumerateArray().ToList();
        var correction = events.Last();
        (correction.GetProperty("type").GetString(), correction.GetProperty("correctsSeq").GetInt32()).ShouldBe(("Correction", handedOverSeq));
        events.Single(e => e.GetProperty("seq").GetInt32() == handedOverSeq).GetProperty("type").GetString().ShouldBe("HandedOver");
        (await HandoversAsync(R12)).Single(h => !h.IsActive).HandedOverOn.ShouldBe(yesterday);
        (await EventPayloadAsync(R12, handedOverSeq)).ShouldContain(Today(), Case.Sensitive, "the original entry is never changed");
        (corrected.GetProperty("status").GetString(), corrected.GetProperty("failedAttempts").GetInt32()).ShouldBe(("AwaitingHandover", 1));

        // The claimant sees the new centre.
        var claimant = (await ClaimantViewAsync(R12Reference)).GetProperty("remedy");
        claimant.GetProperty("repairCentre").GetProperty("name").GetString().ShouldBe("Aurora Service Center Riverside");
        claimant.GetRawText().ShouldNotContain(TransferReason);
        claimant.GetRawText().ShouldNotContain(CorrectionReason);

        // At the new centre the same attempt continues; a corrected ready mark never queues a second notification.
        await RecordAsync(agent, R12, HandOver(Today()));
        var ready = await RecordAsync(agent, R12, Event("MarkedReady"));
        var readySeq = ready.GetProperty("events").EnumerateArray().Last().GetProperty("seq").GetInt32();
        var undone = await RecordAsync(agent, R12, Event("Correction", body =>
        {
            body["correctsSeq"] = readySeq;
            body["reason"] = "The centre called back: the repair is not finished yet.";
            body["toStatus"] = "AtRepairCentre";
        }));
        undone.GetProperty("status").GetString().ShouldBe("AtRepairCentre");
        (await RecordAsync(agent, R12, Event("MarkedReady"))).GetProperty("status").GetString().ShouldBe("ReadyForCollection");

        var attempts = await AttemptsAsync(R12);
        attempts.Count.ShouldBe(2);
        (await NotificationKeysAsync(R12)).ShouldHaveSingleItem().ShouldBe($"repair-ready:{await AttemptIdAsync(R12, 2)}");
        (await FailedAttemptsAsync(R12)).ShouldBe(1);
        (await ClaimStatusAsync(R12)).ShouldBe(Approved);
    }

    // ---- Refusals (on R1, unchanged) ----------------------------------------------------------------

    [Fact]
    public async Task A_transfer_to_a_Borealis_centre_from_an_Aurora_claim_is_not_found_and_records_ACCESS_DENIED()
    {
        var before = await RepairAsync(TestStaffUsers.AgentAurora, R1);
        var handoversBefore = (await HandoversAsync(R1)).Count;
        var deniedBefore = await AccessDeniedCountAsync(BorealisLyon);

        var response = await RepairEventAsync(TestStaffUsers.AgentAurora, R1, ETagOf(before), Event("Transferred", body =>
        {
            body["repairCentreId"] = BorealisLyon;
            body["reason"] = "The customer moved to Lyon for work.";
        }));

        response.Status.ShouldBe(HttpStatusCode.NotFound, response.Body);
        response.Body.ShouldNotContain("Borealis");
        response.Body.ShouldNotContain("Lyon");
        (await AccessDeniedCountAsync(BorealisLyon)).ShouldBe(deniedBefore + 1);
        (await HandoversAsync(R1)).Count.ShouldBe(handoversBefore);
        (await RepairAsync(TestStaffUsers.AgentAurora, R1)).GetProperty("etag").GetString().ShouldBe(ETagOf(before));
    }

    [Fact]
    public async Task Transfers_and_corrections_without_a_reason_or_an_entry_to_correct_are_refused_naming_the_field()
    {
        var before = await RepairAsync(TestStaffUsers.AgentAurora, R1);
        var suggestedSeq = before.GetProperty("events").EnumerateArray().First().GetProperty("seq").GetInt32();
        var date = Today();

        var cases = new (JsonObject Body, string Field)[]
        {
            (Event("Transferred", body => body["repairCentreId"] = Riverside), "reason"),
            (Event("Transferred", body => body["reason"] = TransferReason), "repairCentreId"),
            (Event("Correction", body => { body["correctsSeq"] = suggestedSeq; body["handedOverOn"] = date; }), "reason"),
            (Event("Correction", body => { body["reason"] = CorrectionReason; body["handedOverOn"] = date; }), "correctsSeq"),
            (Event("Correction", body => { body["correctsSeq"] = 999; body["reason"] = CorrectionReason; body["handedOverOn"] = date; }), "correctsSeq"),
            (Event("Correction", body => { body["correctsSeq"] = suggestedSeq; body["reason"] = CorrectionReason; }), "correction"),
            (Event("Correction", body => { body["correctsSeq"] = suggestedSeq; body["reason"] = CorrectionReason; body["handedOverOn"] = date; }), "correctsSeq"),
        };

        foreach (var (body, field) in cases)
        {
            var response = await RepairEventAsync(TestStaffUsers.AgentAurora, R1, ETagOf(before), body);
            response.Status.ShouldBe(HttpStatusCode.BadRequest, $"{body.ToJsonString()}: {response.Body}");
            response.Body.ShouldContain(field, Case.Sensitive, body.ToJsonString());
        }

        (await RepairAsync(TestStaffUsers.AgentAurora, R1)).GetProperty("etag").GetString().ShouldBe(ETagOf(before));
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    private static JsonObject HandOver(string handedOverOn) => Event("HandedOver", body =>
    {
        body["reportedAt"] = DateTimeOffset.UtcNow.AddMinutes(-30);
        body["handedOverOn"] = handedOverOn;
        body["centreReference"] = "JOB-5012";
    });

    /// <summary>A <c>RepairEventRequest</c> reported by phone a minute ago, adjusted by <paramref name="adjust"/>.</summary>
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

    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string ETagOf(JsonElement repair) => repair.GetProperty("etag").GetString().ShouldNotBeNull();

    // ---- API ---------------------------------------------------------------------------------------

    private async Task<JsonElement> RepairAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("repair");
    }

    /// <summary>Records <paramref name="body"/> with the repair's current ETag and returns the updated repair (201).</summary>
    private async Task<JsonElement> RecordAsync(TestStaffUser user, Guid claimId, JsonObject body)
    {
        var response = await RepairEventAsync(user, claimId, ETagOf(await RepairAsync(user, claimId)), body);
        response.Status.ShouldBe(HttpStatusCode.Created, $"{body.ToJsonString()}: {response.Body}");
        return JsonSerializer.Deserialize<JsonElement>(response.Body);
    }

    private async Task<PostResponse> RepairEventAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/repair/events") { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await client.SendAsync(request, Ct);
        return new PostResponse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The claimant view on the Aurora channel, opened with the reference and the seeded customer's email.</summary>
    private async Task<JsonElement> ClaimantViewAsync(string reference)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = AuroraCustomer }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<List<HandoverRow>> HandoversAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select h.service_center_id, h.is_active, h.handed_over_on
            from claims.repair_handovers h join claims.repairs r on r.id = h.repair_id
            where r.claim_id = @id order by h.recorded_at
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<HandoverRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HandoverRow(reader.GetGuid(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateOnly>(2)));
        }

        return rows;
    }

    private async Task<List<AttemptRow>> AttemptsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select a.seq, a.result, a.service_center_id
            from claims.repair_attempts a join claims.repairs r on r.id = a.repair_id
            where r.claim_id = @id order by a.seq
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<AttemptRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new AttemptRow(reader.GetInt32(0), reader.GetString(1), reader.GetGuid(2)));
        }

        return rows;
    }

    private Task<Guid> AttemptIdAsync(Guid claimId, int seq)
        => ScalarAsync<Guid>(
            "select a.id from claims.repair_attempts a join claims.repairs r on r.id = a.repair_id where r.claim_id = @value and a.seq = " + seq,
            claimId);

    private Task<int> FailedAttemptsAsync(Guid claimId)
        => ScalarAsync<int>("select failed_attempts from claims.repairs where claim_id = @value", claimId);

    private Task<string> ClaimStatusAsync(Guid claimId)
        => ScalarAsync<string>("select status from claims.claims where id = @value", claimId);

    private Task<string> EventPayloadAsync(Guid claimId, int seq)
        => ScalarAsync<string>("select payload::text from audit.repair_events where claim_id = @value and seq = " + seq, claimId);

    private async Task<List<string?>> NotificationKeysAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select dedupe_key from integration.notifications where claim_id = @id and template = @template", connection);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("template", ReadyTemplate);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var keys = new List<string?>();
        while (await reader.ReadAsync(Ct))
        {
            keys.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return keys;
    }

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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record PostResponse(HttpStatusCode Status, string Body);

    private sealed record HandoverRow(Guid ServiceCenterId, bool IsActive, DateOnly? HandedOverOn);

    private sealed record AttemptRow(int Seq, string Result, Guid ServiceCenterId);
}
