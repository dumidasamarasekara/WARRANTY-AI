using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using Warranty.IntegrationTests.Scenarios;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Feature 002 user story 6 (quickstart R14, R15; FR-006, FR-036, FR-037, research R9): a claimant states an
/// additional protection plan. R14 — Borealis lets a confirmed plan lift its accidental-damage limits: on serial
/// BS11-24-0015, whose one included accidental-damage incident is used by the seeded history claim
/// (<c>seed/tenants/borealis/historical-claims.json</c>), the S4 screen crack with a plan document goes to human
/// review with the reason "protection plan to be checked"; the reviewer ticks "Protection plan checked" and approves,
/// and the remedy comes from row <c>protectionPlan</c>. The same claim without the document (serial BS11-24-0019) is
/// pending information asking for the plan document. R15 — Aurora does not let a plan lift its exclusion: the S4
/// claim with a plan (serial AT10-24-0040) keeps the 001 behaviour.
/// <para>
/// The variants replay the S4 recordings (<c>recordingsOf</c> in seed/golden/scenarios.json) on their own serials and
/// evidence files, so they share no serial or file with the 001 S4 claims. The recorded invoices name the S4 serials,
/// so the invoice serial check fails on every variant and adds its own escalation reasons; the tests therefore check
/// the protection-plan reason and check, not the absence of every other reason.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "002")]
public sealed class US6_ProtectionPlanTests(WarrantyAppFixture fixture)
{
    private const string PendingEscalation = "Pending T261";
    private const string PendingReviewerConfirmation = "Pending T262";

    /// <summary>Wire name of the protection-plan guardrail check (T261).</summary>
    private const string PlanCheck = "PROTECTION_PLAN_NOT_RELIED_ON";

    private const string PlanReason = "PROTECTION_PLAN_TO_CHECK";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Seed ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_R14_serial_already_used_its_included_accidental_damage_incident_more_than_90_days_ago()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select c.status, c.final_outcome, c.finalized_at,
                   exists (select 1 from adjudication.adjudication_runs r
                           join adjudication.evidence_findings f on f.tenant_id = r.tenant_id and f.run_id = r.id
                           where r.claim_id = c.id and f.kind = 'PhotoAnalysis'
                             and jsonb_exists(f.result -> 'damageTypes', 'CRACKED_SCREEN')) as shows_accidental_damage
            from claims.claims c
            where c.tenant_id = '22222222-2222-7222-8222-222222222222' and c.serial_number = 'BS11-24-0015'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string Status, string? Outcome, DateTimeOffset? FinalizedAt, bool Accidental)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3)));
        }

        // Only the seeded history claim before any R14 claim is submitted; R14's own claim may already exist.
        var history = rows.Where(r => r.Accidental).ToList().ShouldHaveSingleItem();
        (history.Status, history.Outcome).ShouldBe(("Approved", "Approved"));
        (DateTimeOffset.UtcNow - history.FinalizedAt.ShouldNotBeNull()).TotalDays.ShouldBeGreaterThan(90, "no duplicate-claim signal");
    }

    // ---- R14 without the plan document ----------------------------------------------------------

    [Fact]
    public async Task R14_a_stated_plan_without_its_document_is_pending_information_asking_for_the_plan_document()
    {
        var scenario = GoldenScenario.Load("R14-borealis-no-document");
        scenario.ProtectionPlanDocument.ShouldBeNull("precondition");

        var claimId = await scenario.SubmitOnceAsync(SubmitAsClaimantAsync);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.PendingInformation);
        var claim = await ClaimRowAsync(claimId);
        claim.HasProtectionPlan.ShouldBeTrue();
        (await LatestRunAsync(claimId)).Disposition.ShouldBe(scenario.ExpectedDisposition);

        var view = await ClaimantViewAsync(scenario, claim.Reference);
        view.GetProperty("status").GetString().ShouldBe("PendingInformation");
        view.GetProperty("requestedItems").EnumerateArray().Select(i => i.GetProperty("item").GetString())
            .ShouldBe(scenario.RequestedItems);
    }

    // ---- R14 with the plan document -------------------------------------------------------------

    [Fact(Skip = PendingEscalation)]
    public async Task R14_a_stated_plan_with_its_document_goes_to_review_with_the_reason_protection_plan_to_be_checked()
    {
        var scenario = GoldenScenario.Load("R14-borealis");
        scenario.RecordingsOf.ShouldBe("S4-borealis");

        var claimId = await SubmitR14Async(scenario);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.UnderReview);
        var claim = await ClaimRowAsync(claimId);
        claim.HasProtectionPlan.ShouldBeTrue();
        claim.ProtectionPlanConfirmed.ShouldBeNull("only the approving reviewer confirms the plan");
        (await EvidenceKindsAsync(claimId)).ShouldContain("ProtectionPlanDocument");

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(scenario.ExpectedDisposition);
        var reasons = await EscalationReasonsAsync(run.Id);
        reasons.ShouldContain(PlanReason);
        foreach (var reason in scenario.ExpectedEscalationReasons)
        {
            reasons.ShouldContain(reason);
        }

        var checks = await GuardrailChecksAsync(run.Id);
        foreach (var failed in scenario.FailedGuardrails)
        {
            checks[failed].ShouldBeFalse(failed);
        }

        // The reviewer sees why: the readable reason label.
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerBorealis);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("protection plan to be checked");
    }

    [Fact(Skip = PendingReviewerConfirmation)]
    public async Task R14_the_reviewer_confirms_the_plan_and_approves_and_the_remedy_comes_from_row_protectionPlan()
    {
        var scenario = GoldenScenario.Load("R14-borealis");
        var claimId = await SubmitR14Async(scenario);
        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.UnderReview);

        var decision = await DecideAsync(TestStaffUsers.ReviewerBorealis, claimId, new JsonObject
        {
            ["decision"] = "Approve",
            ["claimantExplanation"] = "Your protection plan covers the accidental damage to your Borealis Slate 11; we are arranging a free repair.",
            ["internalReason"] = "Protection plan document checked: valid plan for this serial, covering accidental damage.",
            ["protectionPlanConfirmed"] = true,
        });

        decision.StatusCode.ShouldBe(HttpStatusCode.OK, decision.Body);
        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe("Approved");
        claim.ProtectionPlanConfirmed.ShouldBe(true);

        var remedy = (await RemedyRowAsync(claimId)).ShouldNotBeNull();
        remedy.Scenario.ShouldBe("ProtectionPlanDamage");
        remedy.Remedy.ShouldBe("Repair");
        remedy.Policy.ShouldBe("BOR-WP v1");
        (await RemedyTableRowsAsync(claimId)).ShouldAllBe(row => row == "protectionPlan");
    }

    // ---- R15: Aurora ----------------------------------------------------------------------------

    [Fact(Skip = PendingEscalation)]
    public async Task R15_at_Aurora_a_plan_changes_nothing_and_the_accidental_damage_exclusion_still_grounds_the_rejection()
    {
        var scenario = GoldenScenario.Load("R15-aurora");
        scenario.RecordingsOf.ShouldBe("S4-aurora");
        scenario.ProtectionPlanDocument.ShouldNotBeNull("precondition");

        var claimId = await scenario.SubmitOnceAsync(SubmitAsClaimantAsync);

        (await WaitUntilSettledAsync(claimId)).ShouldNotBe(ClaimStatus.PendingInformation);
        (await ClaimRowAsync(claimId)).HasProtectionPlan.ShouldBeTrue();
        var run = await LatestRunAsync(claimId);
        (await RecommendationDecisionAsync(run.Id)).ShouldBe(scenario.ExpectedRecommendation);

        var checks = await GuardrailChecksAsync(run.Id);
        foreach (var passed in scenario.PassedGuardrails)
        {
            checks[passed].ShouldBeTrue(passed);
        }

        var reasons = await EscalationReasonsAsync(run.Id);
        foreach (var reason in scenario.AbsentEscalationReasons)
        {
            reasons.ShouldNotContain(reason);
        }

        (await CitedClauseKeysAsync(run.Id)).ShouldBe(scenario.ExpectedClauseKeys, ignoreOrder: true);
    }

    // ---- Submission --------------------------------------------------------------------------------

    /// <summary>The R14 claim with its plan document, shared by the escalation and the reviewer tests.</summary>
    private Task<Guid> SubmitR14Async(GoldenScenario scenario)
    {
        scenario.ProtectionPlanDocument.ShouldNotBeNull("precondition");
        return scenario.SubmitOnceAsync(SubmitAsClaimantAsync);
    }

    /// <summary>Submits through the tenant's claimant channel (the claim JSON states the plan, the form carries its document).</summary>
    private async Task<Guid> SubmitAsClaimantAsync(GoldenScenario scenario)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsync("/api/public/claims", scenario.ToSubmission(), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
        return await ScalarAsync<Guid>("select id from claims.claims where reference = @value", reference);
    }

    private Task<ClaimStatus> WaitUntilSettledAsync(Guid claimId)
        => fixture.WaitForClaimStatusAsync(
            claimId, status => status is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation), TimeSpan.FromSeconds(90));

    private async Task<JsonElement> ClaimantViewAsync(GoldenScenario scenario, string reference)
    {
        using var access = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var token = await access.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = scenario.ContactEmail }, Ct);
        token.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accessToken = (await token.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;

        using var client = fixture.CreateClaimantClient(scenario.ChannelHost, accessToken);
        using var response = await client.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary><c>POST /api/claims/{claimId}/review-decisions</c> as <paramref name="user"/> with the claim's current ETag.</summary>
    private async Task<(HttpStatusCode StatusCode, string Body)> DecideAsync(TestStaffUser user, Guid claimId, JsonObject decision)
    {
        using var client = fixture.CreateStaffClient(user);
        using var detail = await client.GetAsync($"/api/claims/{claimId}", Ct);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/review-decisions") { Content = JsonContent.Create(decision) };
        request.Headers.TryAddWithoutValidation("If-Match", detail.Headers.ETag.ShouldNotBeNull().Tag);
        using var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<ClaimRow> ClaimRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select status, reference, has_protection_plan, protection_plan_confirmed from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} not found");
        return new ClaimRow(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetBoolean(3));
    }

    private async Task<RunRow> LatestRunAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select id, disposition from adjudication.adjudication_runs where claim_id = @id order by round desc limit 1", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no adjudication run");
        return new RunRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<string?> RecommendationDecisionAsync(Guid runId)
        => await ScalarOrNullAsync<string>("select decision from adjudication.recommendations where run_id = @value and is_valid", runId);

    private async Task<List<string>> CitedClauseKeysAsync(Guid runId)
        => await StringsAsync("select clause_key from adjudication.retrieved_policy_refs where run_id = @value and cited order by ref_id", runId);

    private async Task<List<string>> EvidenceKindsAsync(Guid claimId)
        => await StringsAsync("select kind from claims.claim_evidence where claim_id = @value order by uploaded_at", claimId);

    private async Task<List<string>> RemedyTableRowsAsync(Guid claimId)
        => await StringsAsync("select table_row from claims.remedy_decisions where claim_id = @value order by seq", claimId);

    /// <summary>Guardrail check code → passed, from the run's ordered <c>checks</c>.</summary>
    private async Task<Dictionary<string, bool>> GuardrailChecksAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select checks::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        var checks = document.RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("passed").GetBoolean());
        checks.ShouldContainKey(PlanCheck);
        return checks;
    }

    /// <summary>The run's escalation reason codes.</summary>
    private async Task<List<string>> EscalationReasonsAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select reasons::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : r.GetProperty("code").GetString()!)
            .ToList();
    }

    private async Task<RemedyRow?> RemedyRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select r.remedy, r.scenario, p.code || ' v' || v.version
            from claims.claim_remedies r
            join policy.policy_versions v on v.id = r.policy_version_id
            join policy.warranty_policies p on p.id = v.policy_id
            where r.claim_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct)
            ? new RemedyRow(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private async Task<List<string>> StringsAsync(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>Runs <paramref name="sql"/> with <paramref name="value"/> as <c>@value</c> and returns the single result.</summary>
    private async Task<T> ScalarAsync<T>(string sql, object value)
        => await ScalarOrNullAsync<T>(sql, value) is { } result ? result : throw new ShouldAssertException($"'{sql}' returned no {typeof(T).Name}.");

    private async Task<T?> ScalarOrNullAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return await command.ExecuteScalarAsync(Ct) is T result ? result : default;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record ClaimRow(string Status, string Reference, bool HasProtectionPlan, bool? ProtectionPlanConfirmed);

    private sealed record RunRow(Guid Id, string? Disposition);

    private sealed record RemedyRow(string? Remedy, string Scenario, string Policy);
}
