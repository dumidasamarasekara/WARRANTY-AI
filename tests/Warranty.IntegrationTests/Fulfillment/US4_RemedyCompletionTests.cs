using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Feature 002 user story 4 (quickstart R11; FR-035) as changed by 003 research R12: staff record that a Refund was
/// completed through <c>POST /api/claims/{claimId}/remedy-completions</c> (T252) with the remedy ETag of the claim
/// detail; the remedy is then Completed and the claimant sees "Refund completed" (T242). A Replacement no longer
/// completes here: the request is refused (400 naming <c>remedy</c>) and the remedy completes through the
/// <c>HandedToCustomer</c> replacement event (T326, 003 quickstart X20); completion rows with remedy Replacement written
/// before 003 still read as "Replacement handed over".
/// <para>
/// R11 uses the seeded R11 claims (<c>remedy-scenarios.json</c>, T251): RMDYR11A01 at Aurora with Replacement and
/// RMDYR11B01 at Borealis with Refund, each decided by a reviewer after two failed repairs. A seeded claim can be
/// completed only once, so each claim's requests run once per test run (<see cref="CompleteOnceAsync"/>,
/// <see cref="LegacyReplacementOnceAsync"/>) and every test of that claim reads the same outcome, whatever the test
/// order. The seeded R1 claim (remedy Repair) only gets requests that must be refused.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "002")]
public sealed class US4_RemedyCompletionTests(WarrantyAppFixture fixture)
{
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

    /// <summary>The once-per-run refusals and simulated pre-003 completion of R11-aurora.</summary>
    private static readonly ConcurrentDictionary<Guid, Lazy<Task<LegacyReplacementRun>>> LegacyRuns = new();

    private static CancellationToken Ct => CancellationToken.None;

    // ---- R11: a Replacement is not completed by a completion record (003 research R12) -------------

    [Fact]
    public async Task R11_a_Replacement_completion_request_is_refused_and_changes_nothing()
    {
        var run = await LegacyReplacementOnceAsync();

        // Before: Replacement in progress, no completion.
        (run.Before.GetProperty("state").GetString(), run.Before.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Replacement"));
        run.Before.TryGetProperty("completion", out _).ShouldBeFalse();

        // A Replacement completes through the HandedToCustomer replacement event, whoever sends the request.
        ShouldBeRefused(run.Refused, "replacement", HttpStatusCode.BadRequest, "HandedToCustomer");
        ShouldBeRefused(run.Refused, "replacementByReviewer", HttpStatusCode.BadRequest, "HandedToCustomer");
        ShouldBeRefused(run.Refused, "replacementWithReference", HttpStatusCode.BadRequest, "remedy");

        // A Refund is not the current remedy.
        ShouldBeRefused(run.Refused, "refund", HttpStatusCode.Conflict, "Replacement");

        // None of them wrote anything (checked before the pre-003 completion was simulated).
        (run.UnchangedState, run.UnchangedETag).ShouldBe(("InProgress", ETagOf(run.Before)));
        run.CompletionRowsBefore.ShouldBe(0);
    }

    [Fact]
    public async Task R11_a_Replacement_completed_before_003_still_reads_as_completed_for_staff_and_the_claimant()
    {
        var run = await LegacyReplacementOnceAsync();

        // Staff: the remedy view shows the pre-003 completion row.
        var detail = await RemedyAsync(TestStaffUsers.AuditorAurora, R11Aurora.Id);
        (detail.GetProperty("state").GetString(), detail.GetProperty("remedy").GetString()).ShouldBe(("Completed", "Replacement"));
        var completion = detail.GetProperty("completion");
        completion.GetProperty("completedOn").GetString().ShouldBe(run.LegacyCompletedOn);
        completion.TryGetProperty("reference", out _).ShouldBeFalse();
        completion.GetProperty("recordedBy").GetString().ShouldNotBeNullOrWhiteSpace();

        // Claimant: "Replacement handed over".
        var remedy = (await ClaimantViewAsync(R11Aurora)).GetProperty("remedy");
        (remedy.GetProperty("remedy").GetString(), remedy.GetProperty("label").GetString(), remedy.GetProperty("completed").GetBoolean())
            .ShouldBe(("Replacement", "Replacement handed over", true));
        remedy.TryGetProperty("repairCentre", out _).ShouldBeFalse();

        // The completed remedy cannot be completed again, by either remedy.
        var again = await CompletionAsync(TestStaffUsers.AgentAurora, R11Aurora.Id, ETagOf(detail), Completion("Replacement", Today()));
        again.Status.ShouldBe(HttpStatusCode.BadRequest, again.Body);
        var refund = await CompletionAsync(TestStaffUsers.AgentAurora, R11Aurora.Id, ETagOf(detail), Completion("Refund", Today(), RefundReference));
        refund.Status.ShouldBe(HttpStatusCode.Conflict, refund.Body);
        refund.Body.ShouldContain("Completed");
        (await CountAsync("select count(*) from claims.remedy_completions where claim_id = @value", R11Aurora.Id)).ShouldBe(1);
    }

    // ---- 003: a Replacement completes through the HandedToCustomer replacement event --------------

    [Fact(Skip = "Pending T329")]
    public async Task A_Replacement_handed_to_the_customer_completes_the_remedy_without_a_completion_record()
    {
        // An Aurora replacement awaiting its unit, faulty product returned at collection (003 quickstart X1, T321).
        // T326 may point this at a dedicated claim of replacement-scenarios.json if X1 is reserved for the US1 tests.
        var claimId = await ScalarAsync<Guid>(
            $"""
            select claim_id from claims.replacements
            where tenant_id = @value and status = '{WireName.Of(ReplacementStatus.AwaitingUnit)}'
              and return_rule = '{WireName.Of(ReplacementReturnRule.AtCollection)}'
            order by claim_id desc limit 1
            """,
            TestStaffUsers.AuroraTenantId);
        var serial = $"SN-T327-{Guid.NewGuid():N}"[..24];

        var reserved = await ReplacementEventAsync(TestStaffUsers.AgentAurora, claimId, await ReplacementETagAsync(claimId), new JsonObject
        {
            ["type"] = "UnitReserved", ["reportedAt"] = DateTimeOffset.UtcNow.AddMinutes(-2), ["source"] = "Phone", ["serialNumber"] = serial,
        });
        reserved.Status.ShouldBe(HttpStatusCode.Created, reserved.Body);
        JsonSerializer.Deserialize<JsonElement>(reserved.Body).GetProperty("status").GetString().ShouldBe("ReadyForCollection");

        var before = await RemedyAsync(TestStaffUsers.AgentAurora, claimId);
        (before.GetProperty("state").GetString(), before.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Replacement"));

        // The 002 completion request is refused; the hand-over completes the remedy.
        var refused = await CompletionAsync(TestStaffUsers.AgentAurora, claimId, ETagOf(before), Completion("Replacement", Yesterday()));
        refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.Body);

        var handedOver = await ReplacementEventAsync(TestStaffUsers.AgentAurora, claimId, await ReplacementETagAsync(claimId), new JsonObject
        {
            ["type"] = "HandedToCustomer", ["reportedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1), ["source"] = "InPerson",
            ["handedOverOn"] = Today(), ["returnConfirmed"] = true,
        });
        handedOver.Status.ShouldBe(HttpStatusCode.Created, handedOver.Body);
        JsonSerializer.Deserialize<JsonElement>(handedOver.Body).GetProperty("status").GetString().ShouldBe("HandedToCustomer");

        var after = await RemedyAsync(TestStaffUsers.AuditorAurora, claimId);
        (after.GetProperty("state").GetString(), after.GetProperty("remedy").GetString()).ShouldBe(("Completed", "Replacement"));
        after.GetProperty("etag").GetString().ShouldNotBe(ETagOf(before));
        (await CountAsync("select count(*) from claims.remedy_completions where claim_id = @value", claimId)).ShouldBe(0);
        (await CountAsync("select count(*) from catalog.replacement_serials where claim_id = @value", claimId)).ShouldBe(1);
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId)).ShouldBe(WireName.Of(ClaimStatus.Approved));
    }

    // ---- R11: Refund completed with its reference (Borealis) ---------------------------------------

    [Fact]
    public async Task R11_recording_the_refund_completion_with_its_reference_completes_the_Borealis_remedy()
    {
        var run = await CompleteOnceAsync(R11Borealis, TestStaffUsers.ReviewerBorealis, TestStaffUsers.AuditorBorealis, TestStaffUsers.AgentAurora,
            Completion("Refund", Today(), RefundReference));

        (run.Before.GetProperty("state").GetString(), run.Before.GetProperty("remedy").GetString()).ShouldBe(("InProgress", "Refund"));
        run.Before.TryGetProperty("completion", out _).ShouldBeFalse();

        // Refused requests changed nothing (each is checked before the completion is recorded).
        ShouldBeRefused(run.Refused, "noIfMatch", HttpStatusCode.PreconditionFailed);
        ShouldBeRefused(run.Refused, "staleIfMatch", HttpStatusCode.PreconditionFailed);
        ShouldBeRefused(run.Refused, "auditor", HttpStatusCode.Forbidden);
        ShouldBeRefused(run.Refused, "otherTenant", HttpStatusCode.NotFound);
        ShouldBeRefused(run.Refused, "repair", HttpStatusCode.BadRequest, "remedy");
        ShouldBeRefused(run.Refused, "replacement", HttpStatusCode.BadRequest, "HandedToCustomer");
        ShouldBeRefused(run.Refused, "tomorrow", HttpStatusCode.BadRequest, "completedOn");
        ShouldBeRefused(run.Refused, "refundWithoutReference", HttpStatusCode.BadRequest, "reference");
        run.Refused["otherTenant"].Body.ShouldNotContain(R11Borealis.Reference);

        // The completion: 201 with the completed remedy and a new ETag.
        run.Recorded.Status.ShouldBe(HttpStatusCode.Created, run.Recorded.Body);
        var remedy = JsonSerializer.Deserialize<JsonElement>(run.Recorded.Body);
        (remedy.GetProperty("state").GetString(), remedy.GetProperty("remedy").GetString()).ShouldBe(("Completed", "Refund"));
        var completion = remedy.GetProperty("completion");
        (completion.GetProperty("completedOn").GetString(), completion.GetProperty("reference").GetString()).ShouldBe((Today(), RefundReference));
        completion.GetProperty("recordedBy").GetString().ShouldNotBeNullOrWhiteSpace();
        remedy.GetProperty("etag").GetString().ShouldNotBe(ETagOf(run.Before));

        // The same request again with the old ETag cannot apply twice.
        run.Repeated.Status.ShouldBe(HttpStatusCode.PreconditionFailed, run.Repeated.Body);

        await ShouldBeCompletedAsync(R11Borealis.Id, "Refund", Today(), RefundReference, TestStaffUsers.ReviewerBorealis);
        var detail = await RemedyAsync(TestStaffUsers.AuditorBorealis, R11Borealis.Id);
        detail.GetProperty("state").GetString().ShouldBe("Completed");
        detail.GetProperty("etag").GetString().ShouldBe(remedy.GetProperty("etag").GetString());

        // Completing the remedy never changes the claim's outcome (FR-016).
        (await ScalarAsync<string>("select status from claims.claims where id = @value", R11Borealis.Id))
            .ShouldBe(WireName.Of(ClaimStatus.Approved));
    }

    // ---- R11: the claimant view ---------------------------------------------------------------------

    [Fact]
    public async Task R11_the_claimant_sees_Refund_completed()
    {
        (await CompleteOnceAsync(R11Borealis, TestStaffUsers.ReviewerBorealis, TestStaffUsers.AuditorBorealis, TestStaffUsers.AgentAurora,
            Completion("Refund", Today(), RefundReference))).Recorded.Status.ShouldBe(HttpStatusCode.Created);

        var refund = await ClaimantViewAsync(R11Borealis);
        var refundRemedy = refund.GetProperty("remedy");
        (refundRemedy.GetProperty("remedy").GetString(), refundRemedy.GetProperty("label").GetString(), refundRemedy.GetProperty("completed").GetBoolean())
            .ShouldBe(("Refund", "Refund completed", true));
        refund.GetRawText().ShouldNotContain(TestStaffUsers.ReviewerBorealis.Subject);
    }

    // ---- A Repair completes through the Collected repair event ---------------------------------------

    [Fact]
    public async Task A_Repair_remedy_cannot_be_completed_by_a_completion_record()
    {
        var before = await RemedyAsync(TestStaffUsers.AgentAurora, R1);
        before.GetProperty("remedy").GetString().ShouldBe("Repair");

        var repair = await CompletionAsync(TestStaffUsers.AgentAurora, R1, ETagOf(before), Completion("Repair", Today()));
        var replacement = await CompletionAsync(TestStaffUsers.ReviewerAurora, R1, ETagOf(before), Completion("Replacement", Today()));
        var refund = await CompletionAsync(TestStaffUsers.ReviewerAurora, R1, ETagOf(before), Completion("Refund", Today(), RefundReference));

        repair.Status.ShouldBe(HttpStatusCode.BadRequest, repair.Body);
        repair.Body.ShouldContain("Collected");
        replacement.Status.ShouldBe(HttpStatusCode.BadRequest, replacement.Body);
        replacement.Body.ShouldContain("HandedToCustomer");
        refund.Status.ShouldBe(HttpStatusCode.Conflict, refund.Body);
        refund.Body.ShouldContain("Repair");
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

        var refused = new Dictionary<string, PostResponse>(StringComparer.Ordinal)
        {
            ["noIfMatch"] = await CompletionAsync(staff, claim.Id, null, completion),
            ["staleIfMatch"] = await CompletionAsync(staff, claim.Id, "\"1\"", completion),
            ["auditor"] = await CompletionAsync(auditor, claim.Id, etag, completion),
            ["otherTenant"] = await CompletionAsync(otherTenantAgent, claim.Id, etag, completion),
            ["repair"] = await CompletionAsync(staff, claim.Id, etag, Completion("Repair", Today())),
            ["tomorrow"] = await CompletionAsync(staff, claim.Id, etag, Completion(remedy, Tomorrow(), RefundReference)),
            ["replacement"] = await CompletionAsync(staff, claim.Id, etag, Completion("Replacement", Today())),
            ["refundWithoutReference"] = await CompletionAsync(staff, claim.Id, etag, Completion("Refund", Today())),
        };

        var unchanged = await RemedyAsync(staff, claim.Id);
        (unchanged.GetProperty("state").GetString(), ETagOf(unchanged)).ShouldBe(("InProgress", etag), "a refused request changed the remedy");
        (await CountAsync("select count(*) from claims.remedy_completions where claim_id = @value", claim.Id)).ShouldBe(0);

        var recorded = await CompletionAsync(staff, claim.Id, etag, completion);
        var repeated = await CompletionAsync(staff, claim.Id, etag, completion);
        return new CompletionRun(before, refused, recorded, repeated);
    }

    private Task<LegacyReplacementRun> LegacyReplacementOnceAsync()
        => LegacyRuns.GetOrAdd(R11Aurora.Id, _ => new Lazy<Task<LegacyReplacementRun>>(LegacyReplacementAsync)).Value;

    /// <summary>
    /// R11-aurora once per test run: the completion requests that must be refused since 003 (none may change anything),
    /// then a completion row as 002 wrote it before 003 (research R12: such rows stay), written directly to the database.
    /// </summary>
    private async Task<LegacyReplacementRun> LegacyReplacementAsync()
    {
        var before = await RemedyAsync(TestStaffUsers.AgentAurora, R11Aurora.Id);
        var etag = ETagOf(before);
        var refused = new Dictionary<string, PostResponse>(StringComparer.Ordinal)
        {
            ["replacement"] = await CompletionAsync(TestStaffUsers.AgentAurora, R11Aurora.Id, etag, Completion("Replacement", Yesterday())),
            ["replacementByReviewer"] = await CompletionAsync(TestStaffUsers.ReviewerAurora, R11Aurora.Id, etag, Completion("Replacement", Today())),
            ["replacementWithReference"] = await CompletionAsync(
                TestStaffUsers.AgentAurora, R11Aurora.Id, etag, Completion("Replacement", Today(), "DN-55120")),
            ["refund"] = await CompletionAsync(TestStaffUsers.AgentAurora, R11Aurora.Id, etag, Completion("Refund", Today(), RefundReference)),
        };

        var unchanged = await RemedyAsync(TestStaffUsers.AgentAurora, R11Aurora.Id);
        var rows = await CountAsync("select count(*) from claims.remedy_completions where claim_id = @value", R11Aurora.Id);

        var completedOn = Yesterday();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            insert into claims.remedy_completions (id, tenant_id, claim_id, remedy, completed_on, reference, refund_request_id, recorded_by, recorded_at)
            select @id, tenant_id, claim_id, '{WireName.Of(RemedyType.Replacement)}', @completedOn, null, null, @recordedBy, now()
            from claims.claim_remedies where claim_id = @claimId;
            update claims.claim_remedies set state = '{WireName.Of(RemedyState.Completed)}', updated_at = now() where claim_id = @claimId;
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("completedOn", DateOnly.Parse(completedOn, System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("recordedBy", TestStaffUsers.AgentAurora.Subject);
        command.Parameters.AddWithValue("claimId", R11Aurora.Id);
        await command.ExecuteNonQueryAsync(Ct);

        return new LegacyReplacementRun(before, refused, unchanged.GetProperty("state").GetString(), ETagOf(unchanged), rows, completedOn);
    }

    private static void ShouldBeRefused(
        IReadOnlyDictionary<string, PostResponse> refused, string request, HttpStatusCode status, string? bodyContains = null)
    {
        var response = refused[request];
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

    /// <summary>The claim detail's <c>replacement.etag</c> (ReplacementView, 003).</summary>
    private async Task<string> ReplacementETagAsync(Guid claimId)
    {
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("replacement").GetProperty("etag").GetString().ShouldNotBeNull();
    }

    /// <summary><c>POST /api/claims/{claimId}/replacement/events</c> with a <c>ReplacementEventRequest</c> (003).</summary>
    private async Task<PostResponse> ReplacementEventAsync(TestStaffUser user, Guid claimId, string ifMatch, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/replacement/events")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

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

    private sealed record LegacyReplacementRun(
        JsonElement Before,
        IReadOnlyDictionary<string, PostResponse> Refused,
        string? UnchangedState,
        string UnchangedETag,
        long CompletionRowsBefore,
        string LegacyCompletedOn);
}
