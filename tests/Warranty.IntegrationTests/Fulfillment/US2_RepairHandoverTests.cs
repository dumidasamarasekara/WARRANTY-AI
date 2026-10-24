using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Feature 002 user story 2 (quickstart R5, R6, R7, R16, R17): staff record what the repair centre reports through
/// <c>POST /api/claims/{claimId}/repair/events</c> (T241) with the repair ETag of the claim detail (T240); the claimant
/// sees the progress without any staff-only data (T242).
/// <para>
/// R5 and R7 change the repair, so they use fresh Aurora claims: a synthetic claim submitted by <c>agent.aurora</c>,
/// escalated (it matches no recording) and approved by <c>reviewer.aurora</c>, which sets remedy Repair by policy and
/// a repair awaiting hand-over at the suggested centre (US1, T225/T228). The seeded R1 and R2 claims only get requests
/// that must be refused, so other tests can keep reading them. R17 also needs the remedy-decisions endpoint of T230.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "002")]
public sealed class US2_RepairHandoverTests(WarrantyAppFixture fixture)
{
    /// <summary>R17 also posts to the remedy-decisions endpoint (T230).</summary>
    private const string PendingRemedyDecisions = "Pending T230";

    private const string ReadyTemplate = "RepairReadyForCollection";
    private const string StaffNote = "Customer phoned twice about the delay; staff-only note.";

    /// <summary>Seeded R1: Aurora AUR-TAB10, North America; repair awaiting hand-over at Springfield.</summary>
    private static readonly Guid R1 = Guid.Parse("11111111-0005-7000-8000-000000000001");

    /// <summary>Seeded R2: Borealis BOR-SLATE11, Europe; repair awaiting hand-over at Lyon Hub.</summary>
    private static readonly Guid R2 = Guid.Parse("22222222-0005-7000-8000-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- R5: the hand-over -------------------------------------------------------------------------

    [Fact]
    public async Task R5_recording_the_hand_over_puts_the_repair_at_the_centre_and_the_claimant_sees_the_centre_but_not_the_reference()
    {
        var (claimId, reference) = await ApprovedRepairClaimAsync();
        var before = await RepairAsync(TestStaffUsers.AgentAurora, claimId);
        Strings(before.GetProperty("allowedEvents")).ShouldContain("HandedOver");
        var reportedAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        var response = await RepairEventAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(before), HandOver(reportedAt));

        response.Status.ShouldBe(HttpStatusCode.Created, response.Body);
        var repair = JsonSerializer.Deserialize<JsonElement>(response.Body);
        repair.GetProperty("status").GetString().ShouldBe("AtRepairCentre");
        repair.GetProperty("centreReference").GetString().ShouldBe("JOB-1001");
        repair.GetProperty("handedOverOn").GetString().ShouldBe(Today());
        repair.GetProperty("etag").GetString().ShouldNotBe(ETagOf(before));
        Strings(repair.GetProperty("allowedEvents")).ShouldContain("MarkedReady");
        var handedOver = repair.GetProperty("events").EnumerateArray().Last();
        handedOver.GetProperty("type").GetString().ShouldBe("HandedOver");
        handedOver.GetProperty("source").GetString().ShouldBe("Phone");
        handedOver.GetProperty("reportedAt").GetDateTimeOffset().ShouldBe(reportedAt, TimeSpan.FromSeconds(1));
        handedOver.GetProperty("recordedAt").GetDateTimeOffset().ShouldBeGreaterThan(reportedAt);

        // Attempt 1 is open at the active centre; the event row keeps both times.
        (await RepairRowAsync(claimId)).Status.ShouldBe("AtRepairCentre");
        var attempt = (await AttemptsAsync(claimId)).ShouldHaveSingleItem();
        (attempt.Seq, attempt.Result).ShouldBe((1, "Open"));
        var row = (await RepairEventRowsAsync(claimId)).Last();
        (row.Type, row.Source, row.Actor).ShouldBe(("HandedOver", "Phone", TestStaffUsers.AgentAurora.Subject));
        row.ReportedAt.ShouldBe(reportedAt, TimeSpan.FromSeconds(1));

        // The trail has the hand-over with the reported and recorded times.
        var entry = (await TrailAsync(claimId)).Last(e => e.Step == "RepairHandedOver");
        entry.Actor.ShouldBe(TestStaffUsers.AgentAurora.Subject);
        entry.Payload.ShouldContain("reportedAt");
        entry.Payload.ShouldContain("recordedAt");

        // The claimant sees "With repair centre" and the centre's public details only.
        var claimant = await ClaimantViewAsync(reference);
        var remedy = claimant.GetProperty("remedy");
        remedy.GetProperty("remedy").GetString().ShouldBe("Repair");
        remedy.GetProperty("label").GetString().ShouldBe("With repair centre");
        remedy.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("state").GetString())
            .ShouldBe(["done", "current", "upcoming", "upcoming"]);
        var centre = remedy.GetProperty("repairCentre");
        centre.GetProperty("name").GetString().ShouldBe("Aurora Service Center Springfield");
        centre.GetProperty("address").GetString().ShouldNotBeNullOrWhiteSpace();
        centre.GetProperty("openingHours").GetString().ShouldNotBeNullOrWhiteSpace();
        centre.TryGetProperty("contact", out _).ShouldBeFalse();
        var text = claimant.GetRawText();
        text.ShouldNotContain("JOB-1001");
        text.ShouldNotContain(StaffNote);
        text.ShouldNotContain(TestStaffUsers.AgentAurora.Subject);
        text.ShouldNotContain(TestStaffUsers.AgentAurora.Username);
    }

    // ---- R6: an event the status does not allow ----------------------------------------------------

    [Fact]
    public async Task R6_marking_ready_before_the_hand_over_conflicts_naming_the_status_and_the_allowed_events()
    {
        var before = await RepairAsync(TestStaffUsers.AgentAurora, R1);
        before.GetProperty("status").GetString().ShouldBe("AwaitingHandover");
        var eventsBefore = await RepairEventRowsAsync(R1);

        var response = await RepairEventAsync(TestStaffUsers.AgentAurora, R1, ETagOf(before), Event("MarkedReady"));

        response.Status.ShouldBe(HttpStatusCode.Conflict, response.Body);
        response.MediaType.ShouldBe("application/problem+json");
        response.Body.ShouldContain("AwaitingHandover");
        response.Body.ShouldContain("HandedOver");
        (await RepairRowAsync(R1)).Status.ShouldBe("AwaitingHandover");
        (await RepairEventRowsAsync(R1)).Count.ShouldBe(eventsBefore.Count);
        (await NotificationsAsync(R1, ReadyTemplate)).ShouldBeEmpty();
    }

    // ---- R7: exactly one ready notification ---------------------------------------------------------

    [Fact]
    public async Task R7_the_same_mark_ready_sent_twice_with_the_old_ETag_changes_the_status_once_and_queues_one_notification()
    {
        var (claimId, _) = await HandedOverClaimAsync();
        var before = await RepairAsync(TestStaffUsers.AgentAurora, claimId);
        var attemptId = (await AttemptsAsync(claimId)).ShouldHaveSingleItem().Id;

        var first = await RepairEventAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(before), Event("MarkedReady"));
        var repeated = await RepairEventAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(before), Event("MarkedReady"));

        first.Status.ShouldBe(HttpStatusCode.Created, first.Body);
        repeated.Status.ShouldBe(HttpStatusCode.PreconditionFailed, repeated.Body);
        await ShouldBeReadyOnceAsync(claimId, attemptId);
    }

    [Fact]
    public async Task R7_two_staff_marking_ready_at_once_change_the_status_once_and_queue_one_notification()
    {
        var (claimId, _) = await HandedOverClaimAsync();
        var before = await RepairAsync(TestStaffUsers.AgentAurora, claimId);
        var attemptId = (await AttemptsAsync(claimId)).ShouldHaveSingleItem().Id;

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => RepairEventAsync(
                i % 2 == 0 ? TestStaffUsers.AgentAurora : TestStaffUsers.ReviewerAurora, claimId, ETagOf(before), Event("MarkedReady"))));

        responses.Count(r => r.Status == HttpStatusCode.Created).ShouldBe(1, string.Join("\n", responses.Select(r => $"{r.Status}: {r.Body}")));
        responses.Where(r => r.Status != HttpStatusCode.Created)
            .ShouldAllBe(r => r.Status == HttpStatusCode.PreconditionFailed || r.Status == HttpStatusCode.Conflict);
        await ShouldBeReadyOnceAsync(claimId, attemptId);

        // A later repeat with the old ETag is refused as well.
        var late = await RepairEventAsync(TestStaffUsers.ReviewerAurora, claimId, ETagOf(before), Event("MarkedReady"));
        late.Status.ShouldBe(HttpStatusCode.PreconditionFailed, late.Body);
        await ShouldBeReadyOnceAsync(claimId, attemptId);
    }

    [Fact]
    public async Task R7_collection_completes_the_repair_and_the_remedy_and_the_claimant_sees_Collected()
    {
        var (claimId, reference) = await HandedOverClaimAsync();
        var ready = await RepairEventAsync(
            TestStaffUsers.AgentAurora, claimId, ETagOf(await RepairAsync(TestStaffUsers.AgentAurora, claimId)), Event("MarkedReady"));
        ready.Status.ShouldBe(HttpStatusCode.Created, ready.Body);
        (await ClaimantViewAsync(reference)).GetProperty("remedy").GetProperty("label").GetString().ShouldBe("Ready for collection");

        var collected = await RepairEventAsync(
            TestStaffUsers.ReviewerAurora, claimId, ETagOf(await RepairAsync(TestStaffUsers.AgentAurora, claimId)), Event("Collected"));

        collected.Status.ShouldBe(HttpStatusCode.Created, collected.Body);
        var repair = JsonSerializer.Deserialize<JsonElement>(collected.Body);
        repair.GetProperty("status").GetString().ShouldBe("Collected");
        repair.GetProperty("allowedEvents").GetArrayLength().ShouldBe(0);
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("Completed");
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId))
            .ShouldBe(WireName.Of(ClaimStatus.Approved));
        var remedy = (await ClaimantViewAsync(reference)).GetProperty("remedy");
        (remedy.GetProperty("label").GetString(), remedy.GetProperty("completed").GetBoolean()).ShouldBe(("Collected", true));
    }

    // ---- R16: tenant isolation ----------------------------------------------------------------------

    [Fact]
    public async Task R16_an_Aurora_agent_gets_404_for_Borealis_repair_centres_and_a_Borealis_centre_on_an_Aurora_claim()
    {
        var borealisCentre = await ScalarAsync<Guid>(
            "select service_center_id from claims.repair_handovers h join claims.repairs r on r.id = h.repair_id "
            + "where r.claim_id = @value and h.is_active", R2);

        // A Borealis claim's centres: not found, ACCESS_DENIED, no Borealis data.
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        using var centres = await client.GetAsync($"/api/claims/{R2}/repair-centres", Ct);
        var centresBody = await centres.Content.ReadAsStringAsync(Ct);
        centres.StatusCode.ShouldBe(HttpStatusCode.NotFound, centresBody);
        centresBody.ShouldNotContain("Borealis");
        centresBody.ShouldNotContain("Lyon");
        (await AccessDeniedCountAsync(R2)).ShouldBeGreaterThan(0);

        // A Borealis centre on an Aurora claim: not found, ACCESS_DENIED, nothing changed.
        var before = await RepairAsync(TestStaffUsers.AgentAurora, R1);
        var handoversBefore = await CountAsync(
            "select count(*) from claims.repair_handovers h join claims.repairs r on r.id = h.repair_id where r.claim_id = @value", R1);
        var deniedBefore = await AccessDeniedCountAsync(borealisCentre);

        var response = await RepairEventAsync(
            TestStaffUsers.AgentAurora, R1, ETagOf(before),
            Event("CentreChanged", body => { body["repairCentreId"] = borealisCentre; body["reason"] = "The customer moved to Lyon for work."; }));

        response.Status.ShouldBe(HttpStatusCode.NotFound, response.Body);
        response.Body.ShouldNotContain("Borealis");
        response.Body.ShouldNotContain("Lyon");
        (await AccessDeniedCountAsync(borealisCentre)).ShouldBe(deniedBefore + 1);
        (await CountAsync(
            "select count(*) from claims.repair_handovers h join claims.repairs r on r.id = h.repair_id where r.claim_id = @value", R1))
            .ShouldBe(handoversBefore);
        (await RepairAsync(TestStaffUsers.AgentAurora, R1)).GetProperty("etag").GetString().ShouldBe(ETagOf(before));
    }

    // ---- R17: the auditor ---------------------------------------------------------------------------

    [Fact(Skip = PendingRemedyDecisions)]
    public async Task R17_an_auditor_reads_the_repair_and_its_trail_but_cannot_record_events_or_decide_the_remedy()
    {
        var (claimId, _) = await HandedOverClaimAsync();
        var ready = await RepairEventAsync(
            TestStaffUsers.AgentAurora, claimId, ETagOf(await RepairAsync(TestStaffUsers.AgentAurora, claimId)), Event("MarkedReady"));
        ready.Status.ShouldBe(HttpStatusCode.Created, ready.Body);

        // Read-only: the detail with the repair and the trace are readable ...
        var repair = await RepairAsync(TestStaffUsers.AuditorAurora, claimId);
        repair.GetProperty("status").GetString().ShouldBe("ReadyForCollection");
        repair.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString())
            .ShouldBe(["CentreSuggested", "HandedOver", "MarkedReady"]);
        using (var client = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora))
        using (var trace = await client.GetAsync($"/api/claims/{claimId}/trace", Ct))
        {
            trace.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await trace.Content.ReadAsStringAsync(Ct);
            body.ShouldContain("RepairHandedOver");
            body.ShouldContain("RepairStatusUpdated");
        }

        // ... but nothing can be recorded.
        var collect = await RepairEventAsync(TestStaffUsers.AuditorAurora, claimId, ETagOf(repair), Event("Collected"));
        collect.Status.ShouldBe(HttpStatusCode.Forbidden, collect.Body);
        var remedyETag = (await DetailAsync(TestStaffUsers.AuditorAurora, claimId)).GetProperty("remedy").GetProperty("etag").GetString();
        var decide = await PostAsync(
            TestStaffUsers.AuditorAurora, $"/api/claims/{claimId}/remedy-decisions", remedyETag,
            new JsonObject { ["remedy"] = "Repair", ["reason"] = "An auditor must not be able to decide a remedy." });
        decide.Status.ShouldBe(HttpStatusCode.Forbidden, decide.Body);
        (await RepairRowAsync(claimId)).Status.ShouldBe("ReadyForCollection");

        // The trail explains the remedy and the repair with actors and times.
        var trail = await TrailAsync(claimId);
        var steps = trail.Select(e => e.Step).ToList();
        string[] expected = ["RemedyRecommended", "RemedyDecided", "RepairCentreAssigned", "RepairHandedOver", "RepairStatusUpdated"];
        expected.Select(step => steps.IndexOf(step)).ShouldAllBe(i => i >= 0);
        expected.Select(step => steps.IndexOf(step)).ShouldBeInOrder(SortDirection.Ascending);
        trail.Single(e => e.Step == "RepairStatusUpdated").Actor.ShouldBe(TestStaffUsers.AgentAurora.Subject);
        trail.Single(e => e.Step == "RepairStatusUpdated").Payload.ShouldContain("reportedAt");
    }

    [Fact]
    public async Task R17_a_failed_attempt_shows_RepairAttemptEnded_in_the_trail_with_its_actor_and_policy_version()
    {
        var (claimId, _) = await HandedOverClaimAsync();

        var failed = await RepairEventAsync(
            TestStaffUsers.AgentAurora, claimId, ETagOf(await RepairAsync(TestStaffUsers.AgentAurora, claimId)),
            Event("RepairFailed", body => body["reason"] = "The logic board fault came back after the first repair."));

        failed.Status.ShouldBe(HttpStatusCode.Created, failed.Body);
        var ended = (await TrailAsync(claimId)).Single(e => e.Step == "RepairAttemptEnded");
        ended.Actor.ShouldBe(TestStaffUsers.AgentAurora.Subject);
        ended.Payload.ShouldContain("AUR-WP");
        using var client = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);
        using var trace = await client.GetAsync($"/api/claims/{claimId}/trace", Ct);
        (await trace.Content.ReadAsStringAsync(Ct)).ShouldContain("RepairAttemptEnded");
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    /// <summary>
    /// A fresh Aurora claim (AUR-TAB10, North America) submitted by <c>agent.aurora</c>, escalated and approved by
    /// <c>reviewer.aurora</c>: remedy Repair by policy, repair awaiting hand-over at the suggested centre.
    /// </summary>
    private async Task<(Guid ClaimId, string Reference)> ApprovedRepairClaimAsync()
    {
        using var agent = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        using var submitted = await agent.PostAsync("/api/claims", Submission(NewSerial()), Ct);
        submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var claimId = (await submitted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("claimId").GetGuid();
        (await fixture.WaitForClaimStatusAsync(claimId, ClaimStatus.UnderReview)).ShouldBe(ClaimStatus.UnderReview);

        using var reviewer = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);
        using var detail = await reviewer.GetAsync($"/api/claims/{claimId}", Ct);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var approval = await PostAsync(
            TestStaffUsers.ReviewerAurora, $"/api/claims/{claimId}/review-decisions", detail.Headers.ETag?.Tag,
            new JsonObject
            {
                ["decision"] = "Approve",
                ["claimantExplanation"] = "Your Aurora Tab 10 is covered by the warranty; we are arranging a free repair at a repair centre.",
            });
        approval.Status.ShouldBe(HttpStatusCode.Created, approval.Body);
        (await RepairRowAsync(claimId)).Status.ShouldBe("AwaitingHandover");

        var reference = await ScalarAsync<string>("select reference from claims.claims where id = @value", claimId);
        return (claimId, reference);
    }

    /// <summary>As <see cref="ApprovedRepairClaimAsync"/>, handed over today by <c>agent.aurora</c> (attempt 1 open).</summary>
    private async Task<(Guid ClaimId, string Reference)> HandedOverClaimAsync()
    {
        var (claimId, reference) = await ApprovedRepairClaimAsync();
        var repair = await RepairAsync(TestStaffUsers.AgentAurora, claimId);
        var handedOver = await RepairEventAsync(
            TestStaffUsers.AgentAurora, claimId, ETagOf(repair), HandOver(DateTimeOffset.UtcNow.AddMinutes(-30)));
        handedOver.Status.ShouldBe(HttpStatusCode.Created, handedOver.Body);
        return (claimId, reference);
    }

    private static JsonObject HandOver(DateTimeOffset reportedAt) => Event("HandedOver", body =>
    {
        body["reportedAt"] = reportedAt;
        body["handedOverOn"] = Today();
        body["centreReference"] = "JOB-1001";
        body["note"] = StaffNote;
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

    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task ShouldBeReadyOnceAsync(Guid claimId, Guid attemptId)
    {
        (await RepairRowAsync(claimId)).Status.ShouldBe("ReadyForCollection");
        (await RepairEventRowsAsync(claimId)).Count(e => e.Type == "MarkedReady").ShouldBe(1);
        var notifications = await NotificationsAsync(claimId, ReadyTemplate);
        notifications.ShouldHaveSingleItem().ShouldBe($"repair-ready:{attemptId}");
    }

    private static string ETagOf(JsonElement repair) => repair.GetProperty("etag").GetString().ShouldNotBeNull();

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    // ---- API ---------------------------------------------------------------------------------------

    private async Task<JsonElement> DetailAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>The claim detail's <c>repair</c> (RepairView, T240).</summary>
    private async Task<JsonElement> RepairAsync(TestStaffUser user, Guid claimId)
        => (await DetailAsync(user, claimId)).GetProperty("repair");

    private Task<PostResponse> RepairEventAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
        => PostAsync(user, $"/api/claims/{claimId}/repair/events", ifMatch, body);

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

    /// <summary>The claimant view on the Aurora channel, opened with the reference and the submitted email.</summary>
    private async Task<JsonElement> ClaimantViewAsync(string reference)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = ContactEmail }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<RepairRow> RepairRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select status, failed_attempts from claims.repairs where claim_id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no repair");
        return new RepairRow(reader.GetString(0), reader.GetInt32(1));
    }

    private async Task<List<AttemptRow>> AttemptsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select a.id, a.seq, a.result
            from claims.repair_attempts a join claims.repairs r on r.id = a.repair_id
            where r.claim_id = @id order by a.seq
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<AttemptRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new AttemptRow(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2)));
        }

        return rows;
    }

    private async Task<List<RepairEventRow>> RepairEventRowsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select type, source, actor, reported_at from audit.repair_events where claim_id = @id order by seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<RepairEventRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new RepairEventRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return rows;
    }

    /// <summary>The dedupe keys of the claim's notifications with <paramref name="template"/>.</summary>
    private async Task<List<string?>> NotificationsAsync(Guid claimId, string template)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select dedupe_key from integration.notifications where claim_id = @id and template = @template", connection);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("template", template);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var keys = new List<string?>();
        while (await reader.ReadAsync(Ct))
        {
            keys.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return keys;
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

    /// <summary>Tenant-visible <c>ACCESS_DENIED</c> events whose target names <paramref name="id"/>.</summary>
    private Task<long> AccessDeniedCountAsync(Guid id)
        => CountAsync(
            $"select count(*) from audit.security_events where kind = '{WireName.Of(SecurityEventKind.AccessDenied)}' "
            + "and target like '%' || @value || '%'",
            id.ToString());

    private Task<long> CountAsync(string sql, object value) => ScalarAsync<long>(sql, value);

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

    private sealed record PostResponse(HttpStatusCode Status, string? MediaType, string Body);

    private sealed record RepairRow(string Status, int FailedAttempts);

    private sealed record AttemptRow(Guid Id, int Seq, string Result);

    private sealed record RepairEventRow(string Type, string Source, string Actor, DateTimeOffset ReportedAt);

    private sealed record TrailRow(string Step, string Actor, string Payload);
}
