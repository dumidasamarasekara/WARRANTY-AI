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
/// Feature 002 user story 3 (quickstart R4, R8, R9, R10): a repair that fails or is not economical sends the remedy back
/// to a reviewer according to the claim's own policy version, while the claim stays <c>Approved</c> and the history keeps
/// the original remedy and every attempt. Staff record the failures and estimates through
/// <c>POST /api/claims/{claimId}/repair/events</c> (T248); the reviewer confirms the remedy through
/// <c>POST /api/claims/{claimId}/remedy-decisions</c> (T249) from the review queue's remedy items (T228).
/// <para>
/// Every case drives its own seeded claim (<c>seed/tenants/*/remedy-scenarios.json</c>, repair awaiting hand-over at the
/// suggested centre), so the cases do not depend on each other's order: R4 uses the "same facts" pair (and covers R8, with
/// its refusal of a new attempt at the limit, and the failure half of R9), the reviewer confirmation of R9 uses R9-borealis and R10 uses R10-borealis.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "002")]
public sealed class US3_FailedRepairTests(WarrantyAppFixture fixture)
{
    private const string FirstFailure = "The display flickers again after the panel was replaced.";
    private const string SecondFailure = "The new panel shows the same fault after one day of use.";
    private const string ConfirmationReason = "Two repairs failed; the Borealis policy gives the customer a refund.";

    /// <summary>Seeded R4-aurora: AUR-TAB10, Europe, AUR-WP (repairFailed after 2 → Replacement).</summary>
    private static readonly Guid R4Aurora = Guid.Parse("11111111-0005-7000-8000-000000000002");

    /// <summary>Seeded R4-borealis: the same facts at Borealis, BOR-WP v1 (repairFailed after 2 → Refund).</summary>
    private static readonly Guid R4Borealis = Guid.Parse("22222222-0005-7000-8000-000000000002");

    /// <summary>Seeded R9-borealis: BOR-SLATE11, Europe; driven to the attempt limit and confirmed by the reviewer.</summary>
    private static readonly Guid R9Borealis = Guid.Parse("22222222-0005-7000-8000-000000000009");

    /// <summary>Seeded R10-borealis: BOR-SLATE11 (claim value 480, limit 60 % = 288.00), Europe.</summary>
    private static readonly Guid R10Borealis = Guid.Parse("22222222-0005-7000-8000-000000000010");

    private const string R10Reference = "RMDYR10B01";
    private const string BorealisCustomer = "remedy.customer@borealis.example.com";

    private static readonly string Approved = WireName.Of(ClaimStatus.Approved);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- R4 / R8 / R9: the same facts reach the attempt limit -----------------------------------------

    [Fact]
    public async Task R4_R8_the_same_facts_reach_the_attempt_limit_and_each_company_recommends_its_own_remedy()
    {
        var aurora = await FailTwiceAsync(TestStaffUsers.AgentAurora, R4Aurora);
        var borealis = await FailTwiceAsync(TestStaffUsers.AgentBorealis, R4Borealis);

        ShouldAwaitConfirmation(aurora, "AUR-WP", "Replacement");
        ShouldAwaitConfirmation(borealis, "BOR-WP", "Refund");
        aurora.GetProperty("policyVersion").GetProperty("version").GetInt32().ShouldBe(2);
        borealis.GetProperty("policyVersion").GetProperty("version").GetInt32().ShouldBe(1);

        // Each evaluation is traced to the claim's own policy version and its repairFailed row.
        foreach (var claimId in new[] { R4Aurora, R4Borealis })
        {
            var rows = await DecisionRowsAsync(claimId);
            rows.ShouldAllBe(r => r.PolicyVersionId == rows[0].PolicyVersionId);
            rows.Where(r => r.TableRow == "repairFailed").Select(r => (r.Kind, r.FailedAttempts)).ShouldBe(
                [("Evaluated", 1), ("Evaluated", 2)]);
        }

        (await DecisionRowsAsync(R4Aurora))[0].PolicyVersionId.ShouldNotBe((await DecisionRowsAsync(R4Borealis))[0].PolicyVersionId);
    }

    // ---- R9: the reviewer confirms the remedy after the limit -----------------------------------------

    [Fact]
    public async Task R9_the_Borealis_reviewer_confirms_the_refund_from_the_queue_and_the_history_keeps_the_repair()
    {
        var remedy = await FailTwiceAsync(TestStaffUsers.AgentBorealis, R9Borealis);
        ShouldAwaitConfirmation(remedy, "BOR-WP", "Refund");

        // The claim is in the review queue as a remedy item.
        var item = (await ReviewQueueAsync(TestStaffUsers.ReviewerBorealis))
            .Single(i => i.GetProperty("claimId").GetGuid() == R9Borealis);
        item.GetProperty("kind").GetString().ShouldBe("RemedyDecision");
        item.GetProperty("remedyPendingReason").GetString().ShouldBe("REPAIR_FAILED_LIMIT");

        // A single recommended remedy still needs the reviewer, who confirms it with a reason (FR-031).
        var confirmed = await PostAsync(
            TestStaffUsers.ReviewerBorealis, $"/api/claims/{R9Borealis}/remedy-decisions", remedy.GetProperty("etag").GetString(),
            new JsonObject { ["remedy"] = "Refund", ["reason"] = ConfirmationReason });

        confirmed.Status.ShouldBe(HttpStatusCode.Created, confirmed.Body);
        var detail = await DetailAsync(TestStaffUsers.ReviewerBorealis, R9Borealis);
        var after = detail.GetProperty("remedy");

        // The repair is closed: it keeps its status and attempts, and no further repair event is allowed (T249).
        var closed = detail.GetProperty("repair");
        closed.GetProperty("status").GetString().ShouldBe("RepairNotPossible");
        closed.GetProperty("allowedEvents").EnumerateArray().ShouldBeEmpty();
        (after.GetProperty("state").GetString(), after.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Refund"));
        (await ClaimStatusAsync(R9Borealis)).ShouldBe(Approved);

        // History: the original Repair, the evaluations of both failures and the reviewer's decision with its reason.
        var history = after.GetProperty("history").EnumerateArray().ToList();
        history.First(h => h.GetProperty("kind").GetString() == "Decided").GetProperty("remedy").GetString().ShouldBe("Repair");
        var decided = history.Last();
        (decided.GetProperty("kind").GetString(), decided.GetProperty("remedy").GetString(), decided.GetProperty("decidedBy").GetString())
            .ShouldBe(("Decided", "Refund", "Reviewer"));
        decided.GetProperty("reason").GetString().ShouldBe(ConfirmationReason);
        (await AttemptResultsAsync(R9Borealis)).ShouldBe(["Failed", "Failed"]);

        // The simulated refund request is recorded with the decision.
        (await TrailAsync(R9Borealis)).Last(e => e.Step == "RemedyDecided").Payload.ShouldContain("Refund");
        (await ReviewQueueAsync(TestStaffUsers.ReviewerBorealis)).ShouldNotContain(i => i.GetProperty("claimId").GetGuid() == R9Borealis);
    }

    // ---- R10: an estimate above the economic limit ----------------------------------------------------

    [Fact]
    public async Task R10_an_estimate_at_60_percent_continues_and_one_cent_more_needs_a_reviewer_without_counting_a_failure()
    {
        var repair = await RepairAsync(TestStaffUsers.AgentBorealis, R10Borealis);
        repair.GetProperty("status").GetString().ShouldBe("AwaitingHandover");
        repair.GetProperty("economicLimitAmount").GetDecimal().ShouldBe(288.00m);
        (await RepairEventAsync(TestStaffUsers.AgentBorealis, R10Borealis, ETagOf(repair), HandOver())).Status
            .ShouldBe(HttpStatusCode.Created);

        // Missing or invalid fields are refused naming the field, without changing the repair.
        var handedOver = await RepairAsync(TestStaffUsers.AgentBorealis, R10Borealis);
        var noAmount = await RepairEventAsync(TestStaffUsers.AgentBorealis, R10Borealis, ETagOf(handedOver), Event("EstimateReported"));
        var negative = await RepairEventAsync(
            TestStaffUsers.AgentBorealis, R10Borealis, ETagOf(handedOver), Event("EstimateReported", body => body["estimateAmount"] = -1m));
        var noReason = await RepairEventAsync(TestStaffUsers.AgentBorealis, R10Borealis, ETagOf(handedOver), Event("RepairFailed"));
        foreach (var (response, field) in new[] { (noAmount, "estimateAmount"), (negative, "estimateAmount"), (noReason, "reason") })
        {
            response.Status.ShouldBe(HttpStatusCode.BadRequest, response.Body);
            response.Body.ShouldContain(field);
        }

        (await RepairAsync(TestStaffUsers.AgentBorealis, R10Borealis)).GetProperty("etag").GetString().ShouldBe(ETagOf(handedOver));

        // 288.00 = 60 % of 480: the repair continues.
        var atLimit = await RecordAsync(
            TestStaffUsers.AgentBorealis, R10Borealis, Event("EstimateReported", body => body["estimateAmount"] = 288.00m));
        (atLimit.GetProperty("status").GetString(), atLimit.GetProperty("latestEstimate").GetDecimal()).ShouldBe(("AtRepairCentre", 288.00m));
        Strings(atLimit.GetProperty("allowedEvents")).ShouldContain("MarkedReady");
        (await RemedyAsync(R10Borealis)).GetProperty("state").GetString().ShouldBe("InProgress");

        // 288.01: not economical, no failure counted, the reviewer chooses between the allowed remedies.
        var above = await RecordAsync(
            TestStaffUsers.AgentBorealis, R10Borealis, Event("EstimateReported", body => body["estimateAmount"] = 288.01m));
        (above.GetProperty("status").GetString(), above.GetProperty("notPossibleReason").GetString(), above.GetProperty("failedAttempts").GetInt32())
            .ShouldBe(("RepairNotPossible", "NotEconomical", 0));
        Strings(above.GetProperty("allowedEvents")).ShouldNotContain("NewAttemptStarted");

        var remedy = await RemedyAsync(R10Borealis);
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("scenario").GetString(), remedy.GetProperty("pendingReason").GetString())
            .ShouldBe(("AwaitingDecision", "RepairNotEconomical", "REPAIR_NOT_ECONOMICAL"));
        Strings(remedy.GetProperty("allowedRemedies")).ShouldBe(["Refund", "Replacement"], ignoreOrder: true);
        remedy.TryGetProperty("recommendedRemedy", out _).ShouldBeFalse("a choice has no recommendation");
        remedy.TryGetProperty("remedy", out _).ShouldBeFalse();
        remedy.GetProperty("tableRow").GetString().ShouldBe("repairNotEconomical");
        remedy.GetProperty("history").EnumerateArray().First(h => h.GetProperty("kind").GetString() == "Decided")
            .GetProperty("remedy").GetString().ShouldBe("Repair");

        // The claim stays Approved; the evaluation keeps the estimate; the trail has the ended attempt.
        (await ClaimStatusAsync(R10Borealis)).ShouldBe(Approved);
        (await RepairRowAsync(R10Borealis)).ShouldBe(new RepairRow("RepairNotPossible", "NotEconomical", 0));
        (await AttemptResultsAsync(R10Borealis)).ShouldBe(["NotEconomical"]);
        var evaluated = (await DecisionRowsAsync(R10Borealis)).Last();
        (evaluated.Kind, evaluated.TableRow, evaluated.EstimateAmount, evaluated.FailedAttempts).ShouldBe(
            ("Evaluated", "repairNotEconomical", (decimal?)288.01m, 0));
        var ended = (await TrailAsync(R10Borealis)).Where(e => e.Step == "RepairAttemptEnded").ShouldHaveSingleItem();
        ended.Actor.ShouldBe(TestStaffUsers.AgentBorealis.Subject);
        ended.Payload.ShouldContain("BOR-WP");

        // The claimant sees that the repair is not possible, without the estimate or the staff's names.
        var claimant = await ClaimantViewAsync(WarrantyAppFixture.BorealisHost, R10Reference, BorealisCustomer);
        claimant.GetProperty("remedy").GetProperty("label").GetString().ShouldBe("Repair not possible. Your claim is being reviewed.");
        // Not "288" in the text: a timestamp's fraction of a second can contain it. No number is the estimate or the limit,
        // and no text has "288.0", which a timestamp cannot (its only dot is after the seconds, at most 59).
        Numbers(claimant).ShouldNotContain(n => n == 288.00m || n == 288.01m);
        claimant.GetRawText().ShouldNotContain("288.0");
        claimant.GetRawText().ShouldNotContain(TestStaffUsers.AgentBorealis.Username);
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    /// <summary>
    /// Hands the seeded claim over, fails attempt 1 (checking R8's "1 of 2, new attempt allowed"), starts attempt 2 at
    /// the centre and fails it; returns the remedy after the second failure.
    /// </summary>
    private async Task<JsonElement> FailTwiceAsync(TestStaffUser agent, Guid claimId)
    {
        var repair = await RepairAsync(agent, claimId);
        repair.GetProperty("status").GetString().ShouldBe("AwaitingHandover");
        (await RepairEventAsync(agent, claimId, ETagOf(repair), HandOver())).Status.ShouldBe(HttpStatusCode.Created);

        var first = await RecordAsync(agent, claimId, Event("RepairFailed", body => body["reason"] = FirstFailure));
        (first.GetProperty("status").GetString(), first.GetProperty("notPossibleReason").GetString()).ShouldBe(("RepairNotPossible", "Failed"));
        (first.GetProperty("failedAttempts").GetInt32(), first.GetProperty("attemptLimit").GetInt32()).ShouldBe((1, 2));
        Strings(first.GetProperty("allowedEvents")).ShouldContain("NewAttemptStarted");
        var remedy = await RemedyAsync(claimId);
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Repair"));
        (await ClaimStatusAsync(claimId)).ShouldBe(Approved);

        var second = await RecordAsync(agent, claimId, Event("NewAttemptStarted", body => body["productLocation"] = "AtCentre"));
        (second.GetProperty("status").GetString(), second.GetProperty("failedAttempts").GetInt32()).ShouldBe(("AtRepairCentre", 1));

        var limit = await RecordAsync(agent, claimId, Event("RepairFailed", body => body["reason"] = SecondFailure));
        (limit.GetProperty("status").GetString(), limit.GetProperty("failedAttempts").GetInt32()).ShouldBe(("RepairNotPossible", 2));
        Strings(limit.GetProperty("allowedEvents")).ShouldNotContain("NewAttemptStarted");

        // At the limit no new attempt can start: the remedy waits for the reviewer (409 naming the status).
        var refused = await RepairEventAsync(
            agent, claimId, ETagOf(limit), Event("NewAttemptStarted", body => body["productLocation"] = "AtCentre"));
        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.Body);
        refused.Body.ShouldContain("RepairNotPossible");
        (await RepairRowAsync(claimId)).ShouldBe(new RepairRow("RepairNotPossible", "Failed", 2));

        // The claim stays Approved; both attempts and both ended-attempt trail entries are kept.
        (await ClaimStatusAsync(claimId)).ShouldBe(Approved);
        (await AttemptResultsAsync(claimId)).ShouldBe(["Failed", "Failed"]);
        var ended = (await TrailAsync(claimId)).Where(e => e.Step == "RepairAttemptEnded").ToList();
        ended.Count.ShouldBe(2);
        ended.ShouldAllBe(e => e.Actor == agent.Subject);
        return await RemedyAsync(claimId);
    }

    private static void ShouldAwaitConfirmation(JsonElement remedy, string policyCode, string recommended)
    {
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("scenario").GetString(), remedy.GetProperty("pendingReason").GetString())
            .ShouldBe(("AwaitingDecision", "RepairFailed", "REPAIR_FAILED_LIMIT"));
        remedy.TryGetProperty("remedy", out _).ShouldBeFalse();
        Strings(remedy.GetProperty("allowedRemedies")).ShouldBe([recommended]);
        remedy.GetProperty("recommendedRemedy").GetString().ShouldBe(recommended);
        remedy.GetProperty("tableRow").GetString().ShouldBe("repairFailed");
        remedy.GetProperty("policyVersion").GetProperty("policyCode").GetString().ShouldBe(policyCode);

        // The history keeps the original remedy set by policy before the re-evaluations.
        var history = remedy.GetProperty("history").EnumerateArray().ToList();
        var original = history.First(h => h.GetProperty("kind").GetString() == "Decided");
        (original.GetProperty("remedy").GetString(), original.GetProperty("decidedBy").GetString(), original.GetProperty("scenario").GetString())
            .ShouldBe(("Repair", "Policy", "CoveredDefect"));
        history.Count(h => h.GetProperty("scenario").GetString() == "RepairFailed").ShouldBe(2);
    }

    private static JsonObject HandOver() => Event("HandedOver", body =>
    {
        body["reportedAt"] = DateTimeOffset.UtcNow.AddMinutes(-30);
        body["handedOverOn"] = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        body["centreReference"] = "JOB-3001";
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

    private static string ETagOf(JsonElement repair) => repair.GetProperty("etag").GetString().ShouldNotBeNull();

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    /// <summary>Every number in a JSON value, at any depth.</summary>
    private static IEnumerable<decimal> Numbers(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => [element.GetDecimal()],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Numbers(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Numbers),
        _ => [],
    };

    // ---- API ---------------------------------------------------------------------------------------

    private async Task<JsonElement> DetailAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<JsonElement> RepairAsync(TestStaffUser user, Guid claimId)
        => (await DetailAsync(user, claimId)).GetProperty("repair");

    /// <summary>The claim's remedy as a reviewer of its tenant sees it.</summary>
    private async Task<JsonElement> RemedyAsync(Guid claimId)
    {
        var reviewer = claimId.ToString().StartsWith("11111111", StringComparison.Ordinal)
            ? TestStaffUsers.ReviewerAurora
            : TestStaffUsers.ReviewerBorealis;
        return (await DetailAsync(reviewer, claimId)).GetProperty("remedy");
    }

    /// <summary>Records <paramref name="body"/> with the repair's current ETag and returns the updated repair (201).</summary>
    private async Task<JsonElement> RecordAsync(TestStaffUser user, Guid claimId, JsonObject body)
    {
        var response = await RepairEventAsync(user, claimId, ETagOf(await RepairAsync(user, claimId)), body);
        response.Status.ShouldBe(HttpStatusCode.Created, response.Body);
        return JsonSerializer.Deserialize<JsonElement>(response.Body);
    }

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
        return new PostResponse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<List<JsonElement>> ReviewQueueAsync(TestStaffUser user)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync("/api/review-queue", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();
    }

    /// <summary>The claimant view on <paramref name="host"/>, opened with the reference and the claim's email.</summary>
    private async Task<JsonElement> ClaimantViewAsync(string host, string reference, string email)
    {
        using var client = fixture.CreateClaimantClient(host);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = email }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(host, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<string> ClaimStatusAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select status from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        return (string)(await command.ExecuteScalarAsync(Ct)).ShouldNotBeNull();
    }

    private async Task<RepairRow> RepairRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select status, not_possible_reason, failed_attempts from claims.repairs where claim_id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no repair");
        return new RepairRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2));
    }

    private async Task<List<string>> AttemptResultsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select a.result from claims.repair_attempts a join claims.repairs r on r.id = a.repair_id
            where r.claim_id = @id order by a.seq
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var results = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private async Task<List<DecisionRow>> DecisionRowsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select kind, table_row, policy_version_id, failed_attempts, estimate_amount
            from claims.remedy_decisions where claim_id = @id order by seq
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<DecisionRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new DecisionRow(
                reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4)));
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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record PostResponse(HttpStatusCode Status, string Body);

    private sealed record RepairRow(string Status, string? NotPossibleReason, int FailedAttempts);

    private sealed record DecisionRow(string Kind, string TableRow, Guid PolicyVersionId, int FailedAttempts, decimal? EstimateAmount);

    private sealed record TrailRow(string Step, string Actor, string Payload);
}
