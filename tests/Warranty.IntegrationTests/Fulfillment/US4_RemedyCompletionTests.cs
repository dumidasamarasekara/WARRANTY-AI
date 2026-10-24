using System.Collections.Concurrent;
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
/// Feature 002 user story 4 (quickstart R11; FR-034, FR-035): staff record that a Replacement was handed to the
/// customer or a Refund was completed through <c>POST /api/claims/{claimId}/remedy-completions</c> (T252) with the
/// remedy ETag of the claim detail; the remedy is then Completed and the claimant sees "Replacement handed over" /
/// "Refund completed" (T242).
/// <para>
/// R11 uses the seeded R11 claims (<c>remedy-scenarios.json</c>, T251): RMDYR11A01 at Aurora with Replacement and
/// RMDYR11B01 at Borealis with Refund, each decided by a reviewer after two failed repairs. A seeded claim can be
/// completed only once, so each claim's requests run once per test run (<see cref="CompleteOnceAsync"/>) and every
/// test of that claim reads the same outcome, whatever the test order. The seeded R1 claim (remedy Repair) only gets
/// requests that must be refused.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "002")]
public sealed class US4_RemedyCompletionTests(WarrantyAppFixture fixture)
{
    private const string PendingCompletion = "Pending T252";
    private const string PendingClaimantLabel = "Pending T252 and T242";

    private const string RefundReference = "RF-BOR-2026-0042";

    /// <summary>Seeded R11-aurora: AUR-TAB10, North America; Replacement decided by a reviewer, in progress.</summary>
    private static readonly SeededClaim R11Aurora = new(
        Guid.Parse("11111111-0005-7000-8000-000000000003"), "RMDYR11A01", "remedy.customer@aurora.example.com", WarrantyAppFixture.AuroraHost);

    /// <summary>Seeded R11-borealis: BOR-SLATE11, Europe; Refund decided by a reviewer, in progress.</summary>
    private static readonly SeededClaim R11Borealis = new(
        Guid.Parse("22222222-0005-7000-8000-000000000003"), "RMDYR11B01", "remedy.customer@borealis.example.com", WarrantyAppFixture.BorealisHost);

    /// <summary>Seeded R1: Aurora, remedy Repair set by policy, repair awaiting hand-over.</summary>
    private static readonly Guid R1 = Guid.Parse("11111111-0005-7000-8000-000000000001");

    /// <summary>The once-per-run requests of each seeded R11 claim (the fixture, and so the database, is shared by the collection).</summary>
    private static readonly ConcurrentDictionary<Guid, Lazy<Task<CompletionRun>>> Runs = new();

    private static CancellationToken Ct => CancellationToken.None;

    // ---- R11: Replacement handed to the customer (Aurora) ------------------------------------------

    [Fact(Skip = PendingCompletion)]
    public async Task R11_recording_the_replacement_handed_over_completes_the_Aurora_remedy()
    {
        var run = await CompleteOnceAsync(R11Aurora, TestStaffUsers.AgentAurora, TestStaffUsers.AuditorAurora, TestStaffUsers.AgentBorealis,
            Completion("Replacement", Yesterday()));

        // Before: Replacement in progress, no completion.
        (run.Before.GetProperty("state").GetString(), run.Before.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Replacement"));
        run.Before.TryGetProperty("completion", out _).ShouldBeFalse();

        // Refused requests changed nothing (each is checked before the completion is recorded).
        ShouldBeRefused(run, "noIfMatch", HttpStatusCode.PreconditionFailed);
        ShouldBeRefused(run, "staleIfMatch", HttpStatusCode.PreconditionFailed);
        ShouldBeRefused(run, "auditor", HttpStatusCode.Forbidden);
        ShouldBeRefused(run, "otherTenant", HttpStatusCode.NotFound);
        ShouldBeRefused(run, "repair", HttpStatusCode.BadRequest, "remedy");
        ShouldBeRefused(run, "tomorrow", HttpStatusCode.BadRequest, "completedOn");
        ShouldBeRefused(run, "notTheCurrentRemedy", HttpStatusCode.Conflict, "Replacement");
        run.Refused["otherTenant"].Body.ShouldNotContain(R11Aurora.Reference);

        // The completion: 201 with the completed remedy and a new ETag.
        run.Recorded.Status.ShouldBe(HttpStatusCode.Created, run.Recorded.Body);
        var remedy = JsonSerializer.Deserialize<JsonElement>(run.Recorded.Body);
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("remedy").GetString()).ShouldBe(("Completed", "Replacement"));
        var completion = remedy.GetProperty("completion");
        completion.GetProperty("completedOn").GetString().ShouldBe(Yesterday());
        completion.TryGetProperty("reference", out _).ShouldBeFalse();
        completion.GetProperty("recordedBy").GetString().ShouldNotBeNullOrWhiteSpace();
        remedy.GetProperty("etag").GetString().ShouldNotBe(ETagOf(run.Before));

        // The same request again with the old ETag cannot apply twice.
        run.Repeated.Status.ShouldBe(HttpStatusCode.PreconditionFailed, run.Repeated.Body);

        await ShouldBeCompletedAsync(R11Aurora.Id, "Replacement", Yesterday(), reference: null, TestStaffUsers.AgentAurora);
        var detail = await RemedyAsync(TestStaffUsers.AuditorAurora, R11Aurora.Id);
        detail.GetProperty("state").GetString().ShouldBe("Completed");
        detail.GetProperty("etag").GetString().ShouldBe(remedy.GetProperty("etag").GetString());
    }

    // ---- R11: Refund completed with its reference (Borealis) ---------------------------------------

    [Fact(Skip = PendingCompletion)]
    public async Task R11_recording_the_refund_completion_with_its_reference_completes_the_Borealis_remedy()
    {
        var run = await CompleteOnceAsync(R11Borealis, TestStaffUsers.ReviewerBorealis, TestStaffUsers.AuditorBorealis, TestStaffUsers.AgentAurora,
            Completion("Refund", Today(), RefundReference));

        (run.Before.GetProperty("state").GetString(), run.Before.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Refund"));
        ShouldBeRefused(run, "noIfMatch", HttpStatusCode.PreconditionFailed);
        ShouldBeRefused(run, "staleIfMatch", HttpStatusCode.PreconditionFailed);
        ShouldBeRefused(run, "auditor", HttpStatusCode.Forbidden);
        ShouldBeRefused(run, "otherTenant", HttpStatusCode.NotFound);
        ShouldBeRefused(run, "repair", HttpStatusCode.BadRequest, "remedy");
        ShouldBeRefused(run, "tomorrow", HttpStatusCode.BadRequest, "completedOn");
        ShouldBeRefused(run, "notTheCurrentRemedy", HttpStatusCode.Conflict, "Refund");
        ShouldBeRefused(run, "refundWithoutReference", HttpStatusCode.BadRequest, "reference");

        run.Recorded.Status.ShouldBe(HttpStatusCode.Created, run.Recorded.Body);
        var remedy = JsonSerializer.Deserialize<JsonElement>(run.Recorded.Body);
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("remedy").GetString()).ShouldBe(("Completed", "Refund"));
        var completion = remedy.GetProperty("completion");
        (completion.GetProperty("completedOn").GetString(), completion.GetProperty("reference").GetString()).ShouldBe((Today(), RefundReference));
        run.Repeated.Status.ShouldBe(HttpStatusCode.PreconditionFailed, run.Repeated.Body);

        await ShouldBeCompletedAsync(R11Borealis.Id, "Refund", Today(), RefundReference, TestStaffUsers.ReviewerBorealis);

        // Completing the remedy never changes the claim's outcome (FR-016).
        (await ScalarAsync<string>("select status from claims.claims where id = @value", R11Borealis.Id))
            .ShouldBe(WireName.Of(ClaimStatus.Approved));
    }

    // ---- R11: the claimant view ---------------------------------------------------------------------

    [Fact(Skip = PendingClaimantLabel)]
    public async Task R11_the_claimant_sees_Replacement_handed_over_and_Refund_completed()
    {
        (await CompleteOnceAsync(R11Aurora, TestStaffUsers.AgentAurora, TestStaffUsers.AuditorAurora, TestStaffUsers.AgentBorealis,
            Completion("Replacement", Yesterday()))).Recorded.Status.ShouldBe(HttpStatusCode.Created);
        (await CompleteOnceAsync(R11Borealis, TestStaffUsers.ReviewerBorealis, TestStaffUsers.AuditorBorealis, TestStaffUsers.AgentAurora,
            Completion("Refund", Today(), RefundReference))).Recorded.Status.ShouldBe(HttpStatusCode.Created);

        var replacement = (await ClaimantViewAsync(R11Aurora)).GetProperty("remedy");
        (replacement.GetProperty("remedy").GetString(), replacement.GetProperty("label").GetString(), replacement.GetProperty("completed").GetBoolean())
            .ShouldBe(("Replacement", "Replacement handed over", true));
        replacement.TryGetProperty("repairCentre", out _).ShouldBeFalse();

        var refund = await ClaimantViewAsync(R11Borealis);
        var refundRemedy = refund.GetProperty("remedy");
        (refundRemedy.GetProperty("remedy").GetString(), refundRemedy.GetProperty("label").GetString(), refundRemedy.GetProperty("completed").GetBoolean())
            .ShouldBe(("Refund", "Refund completed", true));
        refund.GetRawText().ShouldNotContain(TestStaffUsers.ReviewerBorealis.Subject);
    }

    // ---- A Repair completes through the Collected repair event ---------------------------------------

    [Fact(Skip = PendingCompletion)]
    public async Task A_Repair_remedy_cannot_be_completed_by_a_completion_record()
    {
        var before = await RemedyAsync(TestStaffUsers.AgentAurora, R1);
        before.GetProperty("remedy").GetString().ShouldBe("Repair");

        var repair = await CompletionAsync(TestStaffUsers.AgentAurora, R1, ETagOf(before), Completion("Repair", Today()));
        var replacement = await CompletionAsync(TestStaffUsers.ReviewerAurora, R1, ETagOf(before), Completion("Replacement", Today()));

        repair.Status.ShouldBe(HttpStatusCode.BadRequest, repair.Body);
        repair.Body.ShouldContain("remedy");
        replacement.Status.ShouldBe(HttpStatusCode.Conflict, replacement.Body);
        replacement.Body.ShouldContain("Repair");
        var after = await RemedyAsync(TestStaffUsers.AgentAurora, R1);
        (after.GetProperty("state").GetString(), ETagOf(after)).ShouldBe(("InProgress", ETagOf(before)));
        (await CountAsync("select count(*) from claims.remedy_completions where claim_id = @value", R1)).ShouldBe(0);
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    /// <summary>
    /// Runs the requests of <paramref name="claim"/> once per test run: first the requests that must be refused (none
    /// may change anything), then the completion by <paramref name="staff"/> with the ETag read before, then the same
    /// request again with that old ETag.
    /// </summary>
    private Task<CompletionRun> CompleteOnceAsync(
        SeededClaim claim, TestStaffUser staff, TestStaffUser auditor, TestStaffUser otherTenantAgent, JsonObject completion)
        => Runs.GetOrAdd(claim.Id, _ => new Lazy<Task<CompletionRun>>(() => RunAsync(claim, staff, auditor, otherTenantAgent, completion))).Value;

    private async Task<CompletionRun> RunAsync(
        SeededClaim claim, TestStaffUser staff, TestStaffUser auditor, TestStaffUser otherTenantAgent, JsonObject completion)
    {
        var before = await RemedyAsync(staff, claim.Id);
        var etag = ETagOf(before);
        var remedy = completion["remedy"]!.GetValue<string>();
        var other = remedy == "Refund" ? "Replacement" : "Refund";

        var refused = new Dictionary<string, PostResponse>(StringComparer.Ordinal)
        {
            ["noIfMatch"] = await CompletionAsync(staff, claim.Id, null, completion),
            ["staleIfMatch"] = await CompletionAsync(staff, claim.Id, "\"1\"", completion),
            ["auditor"] = await CompletionAsync(auditor, claim.Id, etag, completion),
            ["otherTenant"] = await CompletionAsync(otherTenantAgent, claim.Id, etag, completion),
            ["repair"] = await CompletionAsync(staff, claim.Id, etag, Completion("Repair", Today())),
            ["tomorrow"] = await CompletionAsync(staff, claim.Id, etag, Completion(remedy, Tomorrow(), RefundReference)),
            ["notTheCurrentRemedy"] = await CompletionAsync(staff, claim.Id, etag, Completion(other, Today(), RefundReference)),
        };
        if (remedy == "Refund")
        {
            refused["refundWithoutReference"] = await CompletionAsync(staff, claim.Id, etag, Completion("Refund", Today()));
        }

        var unchanged = await RemedyAsync(staff, claim.Id);
        (unchanged.GetProperty("state").GetString(), ETagOf(unchanged)).ShouldBe(("InProgress", etag), "a refused request changed the remedy");
        (await CountAsync("select count(*) from claims.remedy_completions where claim_id = @value", claim.Id)).ShouldBe(0);

        var recorded = await CompletionAsync(staff, claim.Id, etag, completion);
        var repeated = await CompletionAsync(staff, claim.Id, etag, completion);
        return new CompletionRun(before, refused, recorded, repeated);
    }

    private static void ShouldBeRefused(CompletionRun run, string request, HttpStatusCode status, string? bodyContains = null)
    {
        var response = run.Refused[request];
        response.Status.ShouldBe(status, $"{request}: {response.Body}");
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            response.MediaType.ShouldBe("application/problem+json", request);
        }

        if (bodyContains is not null)
        {
            response.Body.ShouldContain(bodyContains, Case.Sensitive, request);
        }
    }

    /// <summary>The database holds one completion row, the remedy Completed and one <c>RemedyCompleted</c> trail entry by the staff member.</summary>
    private async Task ShouldBeCompletedAsync(Guid claimId, string remedy, string completedOn, string? reference, TestStaffUser staff)
    {
        await using var connection = await OpenAsync();
        await using (var command = new NpgsqlCommand(
            """
            select rc.remedy, rc.completed_on::text, rc.reference, rc.recorded_by, rc.refund_request_id is null, r.state
            from claims.remedy_completions rc join claims.claim_remedies r on r.claim_id = rc.claim_id
            where rc.claim_id = @id
            """, connection))
        {
            command.Parameters.AddWithValue("id", claimId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no completion");
            (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetString(5))
                .ShouldBe((remedy, completedOn, reference, staff.Subject, true, "Completed"));
            (await reader.ReadAsync(Ct)).ShouldBeFalse("one completion per claim");
        }

        await using var trail = new NpgsqlCommand(
            "select actor, coalesce(payload::text, '') from audit.decision_trail_entries where claim_id = @id and step = 'RemedyCompleted'", connection);
        trail.Parameters.AddWithValue("id", claimId);
        await using var entries = await trail.ExecuteReaderAsync(Ct);
        (await entries.ReadAsync(Ct)).ShouldBeTrue("no RemedyCompleted trail entry");
        entries.GetString(0).ShouldBe(staff.Subject);
        entries.GetString(1).ShouldContain(remedy);
        (await entries.ReadAsync(Ct)).ShouldBeFalse("one RemedyCompleted trail entry");
    }

    /// <summary>A <c>RemedyCompletionRequest</c>.</summary>
    private static JsonObject Completion(string remedy, string completedOn, string? reference = null)
    {
        var body = new JsonObject { ["remedy"] = remedy, ["completedOn"] = completedOn };
        if (reference is not null)
        {
            body["reference"] = reference;
        }

        return body;
    }

    private static string Today() => Day(0);

    private static string Yesterday() => Day(-1);

    private static string Tomorrow() => Day(1);

    private static string Day(int offset)
        => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string ETagOf(JsonElement remedy) => remedy.GetProperty("etag").GetString().ShouldNotBeNull();

    // ---- API ---------------------------------------------------------------------------------------

    /// <summary>The claim detail's <c>remedy</c> (RemedyView).</summary>
    private async Task<JsonElement> RemedyAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("remedy");
    }

    private async Task<PostResponse> CompletionAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/remedy-completions")
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

    /// <summary>The claimant view on the claim's channel, opened with the reference and the customer's email.</summary>
    private async Task<JsonElement> ClaimantViewAsync(SeededClaim claim)
    {
        using var client = fixture.CreateClaimantClient(claim.ChannelHost);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference = claim.Reference, contact = claim.ContactEmail }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(claim.ChannelHost, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{claim.Reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

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

    private sealed record SeededClaim(Guid Id, string Reference, string ContactEmail, string ChannelHost);

    private sealed record PostResponse(HttpStatusCode Status, string? MediaType, string Body);

    private sealed record CompletionRun(
        JsonElement Before, IReadOnlyDictionary<string, PostResponse> Refused, PostResponse Recorded, PostResponse Repeated);
}
