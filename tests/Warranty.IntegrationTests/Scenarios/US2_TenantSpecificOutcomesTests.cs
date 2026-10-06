using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// User story 2 end to end (quickstart S3, S4, S9 and the US2 catalog and evidence-reuse cases): the same
/// kind of claim gets each tenant's own outcome, grounded only in that tenant's policy clauses, and no
/// tenant's catalog or evidence history is consulted for another. Claims are submitted through the
/// tenant's claimant channel and adjudicated with recorded AI responses (seed/golden/scenarios.json,
/// tests/fixtures/ai-recordings/); every scenario has its own serial and evidence files, so no test trips
/// another's duplicate-serial or evidence-reuse signal — except the cross-tenant reuse case, which shares
/// one photo on purpose.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class US2_TenantSpecificOutcomesTests(WarrantyAppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task S3_an_EU_purchase_after_12_months_is_approved_citing_the_EU_warranty_period_only()
    {
        var scenario = GoldenScenario.Load("S3");

        var claimId = await SubmitOnceAsync(scenario);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.Approved);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);
        claim.Region.ShouldBe("EU");

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoApprove);
        (await RecommendationDecisionAsync(run.Id)).ShouldBe(AiDecision.Approve);
        (await GuardrailChecksAsync(run.Id))["COVERAGE_WINDOW_AGREES"].Passed.ShouldBeTrue();

        // The EU period (AUR-WP-2.2) grounds the approval; the 12-month North America period is not cited.
        var cited = (await CitedPolicyRefsAsync(run.Id)).Select(r => r.ClauseKey).ToList();
        cited.ShouldBe(scenario.ExpectedClauseKeys, ignoreOrder: true);
        cited.ShouldNotContain("AUR-WP-2.1");
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(1);
    }

    [Fact]
    public async Task S4_Aurora_rejects_an_accidental_screen_crack_citing_its_accidental_damage_exclusion()
    {
        var scenario = GoldenScenario.Load("S4-aurora");

        var claimId = await SubmitOnceAsync(scenario);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.Rejected);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Rejected);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoReject);
        (await RecommendationDecisionAsync(run.Id)).ShouldBe(AiDecision.Reject);
        var checks = await GuardrailChecksAsync(run.Id);
        checks["COVERAGE_WINDOW_AGREES"].Passed.ShouldBeTrue();
        checks["GROUNDED_IN_CLAUSE"].Passed.ShouldBeTrue();

        var cited = await CitedPolicyRefsAsync(run.Id);
        cited.Select(r => r.ClauseKey).ShouldContain("AUR-WP-3.1");
        cited.Single(r => r.ClauseKey == "AUR-WP-3.1").ExclusionCode.ShouldBe("ACCIDENTAL_DAMAGE");
        await ShouldOnlyReferenceClausesOfAsync(run.Id, "AUR-WP-");
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(0);
    }

    [Fact]
    public async Task S4_Borealis_approves_the_same_accidental_screen_crack_citing_its_accidental_damage_clause()
    {
        var scenario = GoldenScenario.Load("S4-borealis");

        var claimId = await SubmitOnceAsync(scenario);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.Approved);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoApprove);
        (await RecommendationDecisionAsync(run.Id)).ShouldBe(AiDecision.Approve);
        var checks = await GuardrailChecksAsync(run.Id);
        checks["COVERAGE_WINDOW_AGREES"].Passed.ShouldBeTrue();
        checks["GROUNDED_IN_CLAUSE"].Passed.ShouldBeTrue();

        (await CitedPolicyRefsAsync(run.Id)).Select(r => r.ClauseKey).ShouldContain("BOR-WP-1.2");
        await ShouldOnlyReferenceClausesOfAsync(run.Id, "BOR-WP-");
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(1);
    }

    [Fact]
    public async Task S9_a_battery_bought_under_policy_version_1_is_rejected_citing_the_version_1_battery_clause()
    {
        var scenario = GoldenScenario.Load("S9");

        var claimId = await SubmitOnceAsync(scenario);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.Rejected);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Rejected);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoReject);
        (await RecommendationDecisionAsync(run.Id)).ShouldBe(AiDecision.Reject);
        var checks = await GuardrailChecksAsync(run.Id);
        checks["COVERAGE_WINDOW_AGREES"].Passed.ShouldBeTrue();
        checks["GROUNDED_IN_CLAUSE"].Passed.ShouldBeTrue();

        // Version 1 (battery 6 months) applies by purchase date, not the newer version 2.
        var cited = await CitedPolicyRefsAsync(run.Id);
        cited.Single(r => r.ClauseKey == "AUR-WP-2.3").Version.ShouldBe(1);
        cited.ShouldAllBe(r => r.Version == 1);
        await ShouldOnlyReferenceClausesOfAsync(run.Id, "AUR-WP-");
    }

    [Fact]
    public async Task A_serial_missing_from_the_tenant_catalog_goes_to_review_with_PRODUCT_NOT_IN_CATALOG_and_no_claim_value()
    {
        var scenario = GoldenScenario.Load("US2-not-in-catalog");

        var claimId = await SubmitOnceAsync(scenario);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.UnderReview);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBeNull();

        // The serial is not in Aurora's catalog (and is never looked up in another tenant's), so the claim has no value.
        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.HumanReview);
        (await RiskSignalCodesAsync(run.Id)).ShouldContain("PRODUCT_NOT_IN_CATALOG");

        var checks = await GuardrailChecksAsync(run.Id);
        checks["PRODUCT_IN_CATALOG"].Passed.ShouldBeFalse();
        checks["CLAIM_VALUE_WITHIN_LIMIT"].Passed.ShouldBeFalse();
        checks["CLAIM_VALUE_WITHIN_LIMIT"].Actual.ShouldBe("unknown");
        (await EscalationReasonsAsync(run.Id)).ShouldContain("PRODUCT_NOT_IN_CATALOG");
        await ShouldOnlyReferenceClausesOfAsync(run.Id, "AUR-WP-");
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_photo_already_used_in_a_Borealis_claim_raises_no_EVIDENCE_REUSED_signal_at_Aurora()
    {
        // The Borealis claim goes first and is settled, so its photo hash is on record before Aurora sees the photo.
        var borealisClaimId = await SubmitOnceAsync(GoldenScenario.Load("US2-reuse-across-tenants-borealis"));
        (await WaitUntilSettledAsync(borealisClaimId)).ShouldBe(ClaimStatus.Approved);
        var scenario = GoldenScenario.Load("US2-reuse-across-tenants");
        (await CountAsync(
            "select count(*) from claims.claim_evidence where claim_id = @value and sha256 in (select sha256 from claims.claim_evidence where claim_id <> @value)",
            borealisClaimId)).ShouldBe(0, "the Borealis claim is the first to use the shared photo");

        var claimId = await SubmitOnceAsync(scenario);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.Approved);
        (await CountAsync(
            "select count(*) from claims.claim_evidence where claim_id = @value and sha256 in (select sha256 from claims.claim_evidence where tenant_id <> (select tenant_id from claims.claims where id = @value))",
            claimId)).ShouldBe(1, "the shared photo is the same file in both tenants");

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoApprove);
        (await RiskSignalCodesAsync(run.Id)).ShouldNotContain("EVIDENCE_REUSED");
        (await GuardrailChecksAsync(run.Id))["RISK_LOW"].Passed.ShouldBeTrue();
    }

    /// <summary>Every retrieved and cited clause of the run belongs to the given tenant's policy.</summary>
    private async Task ShouldOnlyReferenceClausesOfAsync(Guid runId, string clausePrefix)
    {
        var keys = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select clause_key from adjudication.retrieved_policy_refs where run_id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            keys.Add(reader.GetString(0));
        }

        keys.ShouldNotBeEmpty();
        keys.ShouldAllBe(k => k.StartsWith(clausePrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Submits the scenario's claim once per test run (through its channel) and returns the claim ID; shared with
    /// the other scenario classes, since US4 compares S22 with this class's S4-aurora claim.
    /// </summary>
    private Task<Guid> SubmitOnceAsync(GoldenScenario scenario) => scenario.SubmitOnceAsync(SubmitAsClaimantAsync);

    /// <summary>Submits through the tenant's claimant channel; the public response carries no claim ID, so it is looked up by reference.</summary>
    private async Task<Guid> SubmitAsClaimantAsync(GoldenScenario scenario)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsync("/api/public/claims", scenario.ToSubmission(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
        return await ScalarAsync<Guid>("select id from claims.claims where reference = @value", reference);
    }

    private Task<ClaimStatus> WaitUntilSettledAsync(Guid claimId)
        => fixture.WaitForClaimStatusAsync(
            claimId, status => status is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation), TimeSpan.FromSeconds(90));

    private async Task<ClaimRow> ClaimRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select final_outcome, final_decided_by, region from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} not found");
        return new ClaimRow(
            reader.IsDBNull(0) ? null : WireName.Parse<FinalOutcome>(reader.GetString(0)),
            reader.IsDBNull(1) ? null : WireName.Parse<DecidedBy>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task<RunRow> LatestRunAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select id, disposition from adjudication.adjudication_runs where claim_id = @id order by round desc limit 1", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no adjudication run");
        return new RunRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : WireName.Parse<Disposition>(reader.GetString(1)));
    }

    /// <summary>The run's recommendation decision; the recommendation must have passed validation.</summary>
    private async Task<AiDecision> RecommendationDecisionAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select decision, is_valid from adjudication.recommendations where run_id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"run {runId} has no recommendation");
        reader.GetBoolean(1).ShouldBeTrue($"the recommendation of run {runId} is invalid");
        return WireName.Parse<AiDecision>(reader.GetString(0));
    }

    private async Task<List<PolicyRefRow>> CitedPolicyRefsAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select ref_id, clause_key, exclusion_code, version from adjudication.retrieved_policy_refs where run_id = @id and cited order by ref_id",
            connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var refs = new List<PolicyRefRow>();
        while (await reader.ReadAsync(Ct))
        {
            refs.Add(new PolicyRefRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3)));
        }

        return refs;
    }

    /// <summary>Guardrail check code → result, from the run's ordered <c>checks</c>.</summary>
    private async Task<Dictionary<string, CheckRow>> GuardrailChecksAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select checks::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().ToDictionary(
            c => c.GetProperty("code").GetString()!,
            c => new CheckRow(
                c.GetProperty("passed").GetBoolean(),
                c.TryGetProperty("actual", out var actual) && actual.ValueKind == JsonValueKind.String ? actual.GetString() : null));
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

    /// <summary>The codes of the run's risk signals (deterministic and AI-reported).</summary>
    private async Task<List<string>> RiskSignalCodesAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select signals::text from adjudication.risk_assessments where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(s => s.GetProperty("code").GetString()!).ToList();
    }

    private Task<long> CountAsync(string sql, object value) => ScalarAsync<long>(sql, value);

    /// <summary>Runs <paramref name="sql"/> with <paramref name="value"/> as <c>@value</c> and returns the single result.</summary>
    private async Task<T> ScalarAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return await command.ExecuteScalarAsync(Ct) is T result ? result : throw new ShouldAssertException($"'{sql}' returned no {typeof(T).Name}.");
    }

    /// <summary>The database owner: reads across tenants without row-level security, for assertions only.</summary>
    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record ClaimRow(FinalOutcome? FinalOutcome, DecidedBy? FinalDecidedBy, string? Region);

    private sealed record RunRow(Guid Id, Disposition? Disposition);

    private sealed record PolicyRefRow(string RefId, string ClauseKey, string? ExclusionCode, int Version);

    private sealed record CheckRow(bool Passed, string? Actual);
}
