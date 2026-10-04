using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// User story 1 end to end (quickstart S1, S2): a claim submitted through the claimant channel or by a
/// claims agent is adjudicated by the harness with recorded AI responses (seed/golden/scenarios.json,
/// tests/fixtures/ai-recordings/) and finalized automatically only through the guardrails. Every claim
/// has its own serial and evidence files, so no test trips another's duplicate-serial or evidence-reuse
/// signal; the S1 claim is submitted once and shared by the S1 and trace tests.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class US1_AutomatedAdjudicationTests(WarrantyAppFixture fixture)
{
    /// <summary>Words and reference IDs that must never reach a claimant (FR-037).</summary>
    private static readonly string[] RiskVocabulary = ["risk", "fraud", "signal", "manipulation", "suspicious", "EV-", "POL-", "GLB-"];

    private static readonly string[] TrailSteps =
    [
        "ClaimSubmitted", "TenantResolved", "EvidenceStored", "IntakeValidated", "ClaimExtracted", "CustomerVerified", "ProductIdentified",
        "PolicyRetrieved", "EvidenceAnalyzed", "CoverageAssessed", "RiskEvaluated", "AiRecommended", "GuardrailsEvaluated", "AutoApproved",
        "AutoRejected", "InformationRequested", "EscalatedToReview", "ReviewerDecided", "SupplementReceived", "ActionExecuted", "AiStepFailed",
        "Correction",
    ];

    /// <summary>Claims submitted once per scenario and shared by the tests that read them.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<Guid>>> SubmittedClaims = new(StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = "Pending T068")]
    public async Task S1_a_clear_defect_within_the_period_is_approved_by_the_system_citing_the_coverage_clause()
    {
        var scenario = GoldenScenario.Load("S1");

        var claimId = await SubmitOnceAsync(scenario);
        var status = await WaitUntilSettledAsync(claimId);

        status.ShouldBe(ClaimStatus.Approved);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);
        claim.FinalExplanation.ShouldNotBeNullOrWhiteSpace();
        claim.FinalizedAt.ShouldNotBeNull();

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoApprove);
        (await RecommendationDecisionAsync(run.Id)).ShouldBe(AiDecision.Approve);

        // The recommendation cites POL-n references that resolve to AUR-WP clauses with version and effective dates.
        var cited = await CitedPolicyRefsAsync(run.Id);
        cited.ShouldNotBeEmpty();
        cited.ShouldAllBe(r => r.RefId.StartsWith("POL-", StringComparison.Ordinal)
                               && r.ClauseKey.StartsWith("AUR-WP-", StringComparison.Ordinal)
                               && r.Version > 0
                               && r.DocumentTitle.Length > 0
                               && r.EffectiveFrom != null);
        cited.Select(r => r.ClauseKey).ShouldBe(scenario.ExpectedClauseKeys, ignoreOrder: true);

        // The approval is executed through the simulated integrations.
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(1);
        (await CountAsync("select count(*) from integration.notifications where claim_id = @value", claimId)).ShouldBeGreaterThanOrEqualTo(1);

        // The claimant sees the outcome and its explanation, never risk data.
        var view = await ClaimantViewAsync(scenario, claim.Reference, scenario.ContactEmail);
        view.GetProperty("status").GetString().ShouldBe("Approved");
        view.GetProperty("outcomeExplanation").GetString().ShouldBe(claim.FinalExplanation);
        ShouldContainNoRiskData(view);
    }

    [Fact(Skip = "Pending T068")]
    public async Task S2_a_claim_after_the_period_is_rejected_by_the_system_with_the_coverage_window_confirmed()
    {
        var scenario = GoldenScenario.Load("S2");

        var claimId = await SubmitOnceAsync(scenario);
        var status = await WaitUntilSettledAsync(claimId);

        status.ShouldBe(ClaimStatus.Rejected);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Rejected);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);
        claim.FinalExplanation.ShouldNotBeNullOrWhiteSpace();

        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoReject);
        (await RecommendationDecisionAsync(run.Id)).ShouldBe(AiDecision.Reject);
        var checks = await GuardrailChecksAsync(run.Id);
        checks["COVERAGE_WINDOW_AGREES"].ShouldBeTrue();
        checks["GROUNDED_IN_CLAUSE"].ShouldBeTrue();
        (await CitedPolicyRefsAsync(run.Id)).Select(r => r.ClauseKey).ShouldContain("AUR-WP-2.1");

        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(0);
        (await CountAsync("select count(*) from integration.notifications where claim_id = @value", claimId)).ShouldBeGreaterThanOrEqualTo(1);

        var view = await ClaimantViewAsync(scenario, claim.Reference, scenario.ContactEmail);
        view.GetProperty("status").GetString().ShouldBe("Rejected");
        view.GetProperty("outcomeExplanation").GetString().ShouldBe(claim.FinalExplanation);
        ShouldContainNoRiskData(view);
    }

    [Fact(Skip = "Pending T058")]
    public async Task Claimant_access_with_the_reference_and_email_returns_the_claim_without_risk_data()
    {
        // A claim of its own; its serial has no replay scenario, so whatever its outcome it touches no other test.
        var scenario = GoldenScenario.Load("S1").With(
            "US1-claimant-access", "AT10-24-0021", "US1/claimant-access-invoice.pdf", ["US1/claimant-access-photo-1.jpg"]);
        var claimId = await SubmitOnceAsync(scenario);
        var claim = await ClaimRowAsync(claimId);

        // The e-mail is compared after normalization, so case does not matter.
        var view = await ClaimantViewAsync(scenario, claim.Reference, scenario.ContactEmail.ToUpperInvariant());

        view.GetProperty("reference").GetString().ShouldBe(claim.Reference);
        Enum.GetNames<ClaimStatus>().ShouldContain(view.GetProperty("status").GetString());
        view.GetProperty("productName").GetString().ShouldBe("Aurora Tab 10");
        ShouldContainNoRiskData(view);
    }

    [Fact(Skip = "Pending T069")] // the claim must also be adjudicated (T068)
    public async Task The_trace_of_S1_lists_every_step_in_order_ending_with_the_automatic_approval()
    {
        var claimId = await SubmitOnceAsync(GoldenScenario.Load("S1"));
        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.Approved);
        using var client = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);

        using var response = await client.GetAsync($"/api/claims/{claimId}/trace", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var trace = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        trace.GetProperty("claimId").GetGuid().ShouldBe(claimId);
        trace.GetProperty("integrity").GetProperty("hashChainValid").GetBoolean().ShouldBeTrue();
        var entries = trace.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("seq").GetInt32()).ShouldBeInOrder(SortDirection.Ascending);
        entries.Select(e => e.GetProperty("occurredAt").GetDateTimeOffset()).ShouldBeInOrder(SortDirection.Ascending);
        entries.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.GetProperty("summary").GetString()));

        var steps = entries.Select(e => e.GetProperty("step").GetString()!).ToList();
        steps.ShouldBeSubsetOf(TrailSteps);
        steps[0].ShouldBe("ClaimSubmitted");
        string[] required =
        [
            "TenantResolved", "EvidenceStored", "IntakeValidated", "ClaimExtracted", "CustomerVerified", "ProductIdentified", "PolicyRetrieved",
            "EvidenceAnalyzed", "CoverageAssessed", "RiskEvaluated", "AiRecommended", "GuardrailsEvaluated", "AutoApproved",
        ];
        foreach (var step in required)
        {
            steps.ShouldContain(step);
        }

        // Evidence and Policy run in parallel, so only the pipeline's fixed points are strictly ordered.
        string[] ordered = ["ClaimSubmitted", "EvidenceStored", "IntakeValidated", "RiskEvaluated", "AiRecommended", "GuardrailsEvaluated", "AutoApproved"];
        ordered.Select(step => steps.IndexOf(step)).ShouldBeInOrder(SortDirection.Ascending);
        steps[steps.IndexOf("GuardrailsEvaluated") + 1].ShouldBe("AutoApproved");
        steps.IndexOf("PolicyRetrieved").ShouldBeLessThan(steps.IndexOf("CoverageAssessed"));
        steps.IndexOf("EvidenceAnalyzed").ShouldBeLessThan(steps.IndexOf("RiskEvaluated"));
    }

    [Fact(Skip = "Pending T068")]
    public async Task A_claims_agent_submission_reaches_the_same_automatic_approval()
    {
        var scenario = GoldenScenario.Load("S1-agent");
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);

        using var response = await client.PostAsync("/api/claims", scenario.ToSubmission(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var claimId = accepted.GetProperty("claimId").GetGuid();
        accepted.GetProperty("round").GetInt32().ShouldBe(1);

        (await WaitUntilSettledAsync(claimId)).ShouldBe(ClaimStatus.Approved);
        var claim = await ClaimRowAsync(claimId);
        claim.Channel.ShouldBe(ClaimChannel.AgentPortal);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);
        var run = await LatestRunAsync(claimId);
        run.Disposition.ShouldBe(Disposition.AutoApprove);
        (await CitedPolicyRefsAsync(run.Id)).Select(r => r.ClauseKey).ShouldBe(scenario.ExpectedClauseKeys, ignoreOrder: true);
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(1);
    }

    [Fact(Skip = "Pending T057")]
    public async Task A_submission_with_a_future_purchase_date_is_a_validation_problem_and_creates_no_claim()
    {
        var scenario = GoldenScenario.Load("S1");
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        const string ClaimsForSerial = "select count(*) from claims.claims where serial_number = @value";
        var before = await CountAsync(ClaimsForSerial, scenario.Serial);
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);

        using var response = await client.PostAsync(
            "/api/public/claims", scenario.ToSubmission(claim => claim["purchase"]!["date"] = future), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name)
            .ShouldContain(name => name.Equals("purchase.date", StringComparison.OrdinalIgnoreCase));
        (await CountAsync(ClaimsForSerial, scenario.Serial)).ShouldBe(before);
    }

    /// <summary>Submits the scenario's claim once per test run (through its channel) and returns the claim ID.</summary>
    private Task<Guid> SubmitOnceAsync(GoldenScenario scenario)
        => SubmittedClaims.GetOrAdd(scenario.ScenarioId, _ => new Lazy<Task<Guid>>(() => SubmitAsClaimantAsync(scenario))).Value;

    /// <summary>Submits through the tenant's claimant channel; the public response carries no claim ID, so it is looked up by reference.</summary>
    private async Task<Guid> SubmitAsClaimantAsync(GoldenScenario scenario)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsync("/api/public/claims", scenario.ToSubmission(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        accepted.TryGetProperty("claimId", out _).ShouldBeFalse("the claimant channel never returns internal IDs");
        accepted.GetProperty("round").GetInt32().ShouldBe(1);
        var reference = accepted.GetProperty("reference").GetString()!;
        reference.Length.ShouldBe(10);

        return await ScalarAsync<Guid>("select id from claims.claims where reference = @value", reference);
    }

    private Task<ClaimStatus> WaitUntilSettledAsync(Guid claimId)
        => fixture.WaitForClaimStatusAsync(
            claimId, status => status is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation), TimeSpan.FromSeconds(90));

    /// <summary>Claimant access (reference + contact → claim token), then the claimant view of the claim.</summary>
    private async Task<JsonElement> ClaimantViewAsync(GoldenScenario scenario, string reference, string contact)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;

        using var withToken = fixture.CreateClaimantClient(scenario.ChannelHost, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>The claimant view (FR-037) has no risk properties and no risk wording or reference IDs anywhere.</summary>
    private static void ShouldContainNoRiskData(JsonElement view)
    {
        foreach (var property in view.EnumerateObject())
        {
            property.Name.ShouldNotContain("risk", Case.Insensitive);
            property.Name.ShouldNotContain("signal", Case.Insensitive);
            property.Name.ShouldNotContain("guardrail", Case.Insensitive);
        }

        var text = view.GetRawText();
        foreach (var term in RiskVocabulary)
        {
            text.ShouldNotContain(term, Case.Insensitive);
        }
    }

    private async Task<ClaimRow> ClaimRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select reference, channel, final_outcome, final_decided_by, final_explanation, finalized_at from claims.claims where id = @id",
            connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} not found");
        return new ClaimRow(
            reader.GetString(0),
            WireName.Parse<ClaimChannel>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : WireName.Parse<FinalOutcome>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : WireName.Parse<DecidedBy>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
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
            """
            select ref_id, clause_key, document_title, version, effective_from, effective_to
            from adjudication.retrieved_policy_refs where run_id = @id and cited order by ref_id
            """, connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var refs = new List<PolicyRefRow>();
        while (await reader.ReadAsync(Ct))
        {
            refs.Add(new PolicyRefRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5)));
        }

        return refs;
    }

    /// <summary>Guardrail check code → passed, from the run's ordered <c>checks</c>.</summary>
    private async Task<Dictionary<string, bool>> GuardrailChecksAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select checks::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("passed").GetBoolean());
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

    private sealed record ClaimRow(
        string Reference, ClaimChannel Channel, FinalOutcome? FinalOutcome, DecidedBy? FinalDecidedBy, string? FinalExplanation, DateTimeOffset? FinalizedAt);

    private sealed record RunRow(Guid Id, Disposition? Disposition);

    private sealed record PolicyRefRow(string RefId, string ClauseKey, string DocumentTitle, int Version, DateOnly? EffectiveFrom, DateOnly? EffectiveTo);
}
