using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// User story 6 end to end (quickstart S1, S2, S11; FR-038 – FR-041): <c>GET /api/claims/{claimId}/trace</c>
/// shows auditors and reviewers every step of a claim's adjudication in <c>seq</c> order — submission,
/// stored evidence, intake validation, extracted data, retrieved policy clauses, evidence findings, risk,
/// the AI recommendation with its confidence and reasoning, the guardrail checks, the routing and the
/// final outcome, and for S11 the reviewer's override — with the AI calls behind each step, and reports
/// whether the stored hash chain still verifies.
/// <para>
/// S1 and S2 are read-only here and share their claims with the US1 tests (<see cref="ScenarioClaims"/>).
/// The S11 override runs on a claim of its own, <c>US6-override</c> (an S5-like claim with the S11
/// decision), so deciding it never touches the S5 claim the US3 tests observe; the tamper test edits
/// one of its entries and restores it before it returns.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class US6_DecisionTrailTests(WarrantyAppFixture fixture)
{
    private const string PendingAiOperations = "Pending T104";

    private const string OverrideScenario = "US6-override";

    /// <summary>The adjudication steps every evaluated claim has, in the order the pipeline guarantees.</summary>
    private static readonly string[] OrderedSteps =
        ["ClaimSubmitted", "EvidenceStored", "IntakeValidated", "RiskEvaluated", "AiRecommended", "GuardrailsEvaluated"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- S1, S2: automatic outcomes -----------------------------------------------------------------

    [Theory]
    [InlineData("S1", "AutoApproved", "Approved")]
    [InlineData("S2", "AutoRejected", "Rejected")]
    public async Task The_trace_of_an_automatic_outcome_holds_every_step_in_seq_order_with_an_intact_hash_chain(
        string scenarioId, string routingStep, string outcome)
    {
        var scenario = GoldenScenario.Load(scenarioId);
        var claimId = await SettledClaimAsync(scenario, Enum.Parse<ClaimStatus>(outcome));

        var trace = await TraceAsync(TestStaffUsers.AuditorAurora, claimId);

        trace.ClaimId.ShouldBe(claimId);
        trace.HashChainValid.ShouldBeTrue();
        ShouldHoldTheEvaluation(trace, scenario);

        // Routing: the guardrails' disposition is executed by the very next entry, which records the final outcome.
        var routing = trace.EntryAfter("GuardrailsEvaluated");
        routing.Step.ShouldBe(routingStep);
        routing.Details.GetProperty("outcome").GetString().ShouldBe(outcome);
        routing.Details.GetProperty("decidedBy").GetString().ShouldBe("System");
        trace.Entries.Skip(trace.Entries.IndexOf(routing) + 1).ShouldAllBe(e => e.Step == "ActionExecuted", "only the executed actions follow the outcome");
        trace.Entries.Select(e => e.Step).ShouldNotContain("ReviewerDecided");
    }

    // ---- S11: a reviewer overrides the AI ------------------------------------------------------------

    [Fact]
    public async Task The_S11_trace_shows_the_escalation_the_reviewers_override_and_the_final_outcome()
    {
        var scenario = GoldenScenario.Load(OverrideScenario);
        var claimId = await DecidedOverrideClaimAsync(scenario);

        var trace = await TraceAsync(TestStaffUsers.AuditorAurora, claimId);

        trace.HashChainValid.ShouldBeTrue();
        ShouldHoldTheEvaluation(trace, scenario);

        // Routing to human review, with the reason.
        var escalated = trace.EntryAfter("GuardrailsEvaluated");
        escalated.Step.ShouldBe("EscalatedToReview");
        escalated.Details.GetProperty("reasons").EnumerateArray().Select(r => r.GetString())
            .ShouldBe(scenario.ExpectedEscalationReasons, ignoreOrder: true);

        // The human review: the reviewer, the decision against the AI, the justification and the claimant explanation.
        var decision = scenario.ReviewerDecision;
        var reviewer = TestStaffUsers.Find(scenario.Reviewer!);
        var decided = trace.Entries.Single(e => e.Step == "ReviewerDecided");
        decided.Seq.ShouldBeGreaterThan(escalated.Seq);
        decided.Actor.ShouldBe(reviewer.Subject);
        decided.Details.GetProperty("decision").GetString().ShouldBe("Reject");
        decided.Details.GetProperty("overridesAi").GetBoolean().ShouldBeTrue();
        decided.Details.GetProperty("justification").GetString().ShouldBe((string)decision["justification"]!);
        decided.Details.GetProperty("claimantExplanation").GetString().ShouldBe((string)decision["claimantExplanation"]!);

        // The final outcome, after which only the executed actions follow.
        decided.Details.GetProperty("status").GetString().ShouldBe("Rejected");
        trace.Entries.Skip(trace.Entries.IndexOf(decided) + 1).ShouldAllBe(e => e.Step == "ActionExecuted", "only the executed actions follow the decision");
        trace.Entries.Select(e => e.Step).ShouldNotContain("AutoApproved");
        trace.Entries.Select(e => e.Step).ShouldNotContain("AutoRejected");

        // The AI recommendation the reviewer overrode is still in the trail as it was.
        trace.Entries.Single(e => e.Step == "AiRecommended").Details.GetProperty("decision").GetString().ShouldBe("APPROVE");
    }

    // ---- AI call summaries -----------------------------------------------------------------------------

    [Theory(Skip = PendingAiOperations)]
    [InlineData("S1", "Approved")]
    [InlineData("S2", "Rejected")]
    public async Task Every_model_call_of_the_claim_is_summarized_on_its_step_with_model_prompt_version_tokens_latency_and_cost(
        string scenarioId, string outcome)
    {
        var claimId = await SettledClaimAsync(GoldenScenario.Load(scenarioId), Enum.Parse<ClaimStatus>(outcome));

        var trace = await TraceAsync(TestStaffUsers.ReviewerAurora, claimId);

        var calls = trace.Entries.SelectMany(e => e.AiCalls.Select(call => (e.Step, Call: call))).ToList();
        calls.Count.ShouldBe((int)await CountAsync("select count(*) from aiops.model_calls where claim_id = @value", claimId));
        foreach (var (step, call) in calls)
        {
            call.GetProperty("agent").GetString().ShouldNotBeNullOrWhiteSpace(step);
            call.GetProperty("model").GetString().ShouldNotBeNullOrWhiteSpace(step);
            call.GetProperty("promptVersion").GetString().ShouldNotBeNullOrWhiteSpace(step);
            call.GetProperty("inputTokens").GetInt32().ShouldBeGreaterThan(0, step);
            call.GetProperty("outputTokens").GetInt32().ShouldBeGreaterThan(0, step);
            call.GetProperty("latencyMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0, step);
            call.GetProperty("estimatedCost").GetDecimal().ShouldBeGreaterThanOrEqualTo(0m, step);
        }

        // The decision call sits on the recommendation, the photo and invoice analyses on the evidence findings.
        trace.Entries.Single(e => e.Step == "AiRecommended").AiCalls.ShouldNotBeEmpty();
        trace.Entries.Single(e => e.Step == "EvidenceAnalyzed").AiCalls.Count.ShouldBeGreaterThanOrEqualTo(3);

        // The retrieval behind the policy clauses: the namespaces searched, the filters and the clauses found.
        var queries = trace.Entries.Single(e => e.Step == "PolicyRetrieved").RagQueries;
        queries.ShouldNotBeEmpty();
        foreach (var query in queries)
        {
            query.GetProperty("namespaces").EnumerateArray().ShouldNotBeEmpty();
            query.GetProperty("filters").ValueKind.ShouldBe(JsonValueKind.Object);
            query.GetProperty("resultClauseKeys").EnumerateArray().ShouldNotBeEmpty();
        }
    }

    // ---- Integrity ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_level_SQL_edit_of_one_entry_makes_hashChainValid_false()
    {
        var claimId = await SettledClaimAsync(GoldenScenario.Load(OverrideScenario), until: _ => true);
        (await TraceAsync(TestStaffUsers.AuditorAurora, claimId)).HashChainValid.ShouldBeTrue();
        var seq = await ScalarAsync<int>(
            "select seq from audit.decision_trail_entries where claim_id = @value and step = 'AiRecommended' order by seq limit 1", claimId);
        var original = await TrailSummaryAsync(claimId, seq);

        try
        {
            // Rewrite the recommendation's summary straight in the database, past the append-only trigger.
            (await SetTrailSummaryAsync(claimId, seq, "AI recommends REJECT (NOT_COVERED, confidence 99).")).ShouldBe(1);

            var tampered = await TraceAsync(TestStaffUsers.AuditorAurora, claimId);

            tampered.HashChainValid.ShouldBeFalse();
            tampered.Entries.Single(e => e.Seq == seq).Summary.ShouldBe("AI recommends REJECT (NOT_COVERED, confidence 99).");
        }
        finally
        {
            await SetTrailSummaryAsync(claimId, seq, original);
        }

        (await TraceAsync(TestStaffUsers.AuditorAurora, claimId)).HashChainValid.ShouldBeTrue("the restored entry verifies again");
    }

    // ---- Access --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("S1", "Approved")]
    [InlineData("S2", "Rejected")]
    public async Task A_claims_agent_gets_403_while_reviewers_and_auditors_of_the_tenant_read_the_trace(string scenarioId, string outcome)
    {
        var claimId = await SettledClaimAsync(GoldenScenario.Load(scenarioId), Enum.Parse<ClaimStatus>(outcome));
        var path = $"/api/claims/{claimId}/trace";

        using var agent = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        using var asAgent = await agent.GetAsync(path, Ct);
        asAgent.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await asAgent.Content.ReadAsStringAsync(Ct)).ShouldNotContain("entries");

        foreach (var user in new[] { TestStaffUsers.ReviewerAurora, TestStaffUsers.AuditorAurora })
        {
            using var client = fixture.CreateStaffClient(user);
            using var response = await client.GetAsync(path, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        }
    }

    // ---- Shared assertions -----------------------------------------------------------------------------

    /// <summary>
    /// The evaluation part of a trace (FR-038): contiguous <c>seq</c> from 1 in chronological order, and the
    /// submission, evidence references, validation, extracted data, retrieved policy references, evidence
    /// findings, risk, recommendation with confidence and reasoning, and guardrail checks of the scenario.
    /// </summary>
    private static void ShouldHoldTheEvaluation(Trace trace, GoldenScenario scenario)
    {
        var entries = trace.Entries;
        entries.Select(e => e.Seq).ShouldBe(Enumerable.Range(1, entries.Count));
        entries.Select(e => e.OccurredAt).ShouldBeInOrder(SortDirection.Ascending);
        entries.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Summary) && !string.IsNullOrWhiteSpace(e.Actor));
        entries[0].Step.ShouldBe("ClaimSubmitted");
        OrderedSteps.Select(step => entries.FindIndex(e => e.Step == step)).ShouldBeInOrder(SortDirection.Ascending);
        entries.FindIndex(e => e.Step == "PolicyRetrieved").ShouldBeLessThan(entries.FindIndex(e => e.Step == "CoverageAssessed"));
        entries.FindIndex(e => e.Step == "EvidenceAnalyzed").ShouldBeLessThan(entries.FindIndex(e => e.Step == "RiskEvaluated"));
        entries.FindIndex(e => e.Step == "ClaimExtracted").ShouldBeLessThan(entries.FindIndex(e => e.Step == "AiRecommended"));

        // Submission.
        var submitted = trace.Single("ClaimSubmitted").Details;
        submitted.GetProperty("reference").GetString().ShouldNotBeNullOrWhiteSpace();
        submitted.GetProperty("channel").GetString().ShouldBe("ClaimantPortal");

        // Evidence references: one stored file per submitted invoice and photo.
        var stored = trace.Single("EvidenceStored").Details.GetProperty("evidence").EnumerateArray().ToList();
        stored.Select(e => e.GetProperty("kind").GetString()).ShouldBe(["Invoice", "Photo", "Photo"], ignoreOrder: true);
        stored.ShouldAllBe(e => e.GetProperty("evidenceId").GetGuid() != Guid.Empty && !string.IsNullOrWhiteSpace(e.GetProperty("sha256").GetString()));

        // Validation and extracted data.
        var checks = trace.Single("IntakeValidated").Details.GetProperty("checks").EnumerateArray().ToList();
        checks.ShouldNotBeEmpty();
        checks.ShouldAllBe(c => !string.IsNullOrWhiteSpace(c.GetProperty("check").GetString()) && c.GetProperty("passed").ValueKind != JsonValueKind.Null);
        var extracted = trace.Single("ClaimExtracted").Details;
        extracted.GetProperty("problemCategory").GetString().ShouldNotBeNullOrWhiteSpace();
        extracted.GetProperty("component").GetString().ShouldNotBeNullOrWhiteSpace();

        // Retrieved policy references with versions and effective dates, the expected clauses among them.
        var clauses = trace.Single("PolicyRetrieved").Details.GetProperty("clauses").EnumerateArray().ToList();
        clauses.ShouldNotBeEmpty();
        clauses.ShouldAllBe(c => c.GetProperty("refId").GetString()!.StartsWith("POL-", StringComparison.Ordinal)
                                 && c.GetProperty("version").GetInt32() > 0
                                 && !string.IsNullOrWhiteSpace(c.GetProperty("effectiveFrom").GetString()));
        var clauseKeys = clauses.Select(c => c.GetProperty("clauseKey").GetString()).ToList();
        scenario.ExpectedClauseKeys.ShouldAllBe(key => clauseKeys.Contains(key));

        // Evidence findings: one per stored file, each under its EV-n reference.
        var findings = trace.Single("EvidenceAnalyzed").Details.GetProperty("findings").EnumerateArray().ToList();
        findings.Count.ShouldBe(stored.Count);
        findings.ShouldAllBe(f => f.GetProperty("evidenceRef").GetString()!.StartsWith("EV-", StringComparison.Ordinal));

        // Risk.
        var risk = trace.Single("RiskEvaluated").Details;
        risk.GetProperty("level").GetString().ShouldBe((string)scenario.Expected["riskLevel"]!);
        risk.GetProperty("signals").ValueKind.ShouldBe(JsonValueKind.Array);

        // Recommendation with confidence and reasoning, its references resolved to clauses and evidence files.
        var recommendation = trace.Single("AiRecommended").Details;
        recommendation.GetProperty("isValid").GetBoolean().ShouldBeTrue();
        recommendation.GetProperty("decision").GetString().ShouldBe((string)scenario.Expected["recommendation"]!);
        recommendation.GetProperty("confidence").GetInt32().ShouldBeInRange(1, 100);
        recommendation.GetProperty("reasoningSummary").GetString().ShouldNotBeNullOrWhiteSpace();
        var cited = recommendation.GetProperty("policyRefs").EnumerateArray().ToList();
        cited.ShouldAllBe(r => r.GetProperty("resolved").GetBoolean());
        var citedKeys = cited.Select(r => r.GetProperty("clauseKey").GetString()).ToList();
        scenario.ExpectedClauseKeys.ShouldAllBe(key => citedKeys.Contains(key));
        recommendation.GetProperty("evidence").EnumerateArray().ShouldAllBe(e => e.GetProperty("resolved").GetBoolean());

        // Guardrail checks with their expected and actual values, and the resulting disposition.
        var guardrails = trace.Single("GuardrailsEvaluated").Details;
        guardrails.GetProperty("disposition").GetString().ShouldBe((string)scenario.Expected["disposition"]!);
        var guardrailChecks = guardrails.GetProperty("checks").EnumerateArray().ToList();
        guardrailChecks.ShouldNotBeEmpty();
        foreach (var check in guardrailChecks)
        {
            check.GetProperty("code").GetString().ShouldNotBeNullOrWhiteSpace();
            check.TryGetProperty("expected", out _).ShouldBeTrue();
            check.TryGetProperty("actual", out _).ShouldBeTrue();
        }

        var failed = guardrailChecks.Where(c => !c.GetProperty("passed").GetBoolean()).Select(c => c.GetProperty("code").GetString()).ToList();
        failed.ShouldBe(scenario.Expected["failedGuardrails"]?.AsArray().Select(c => (string?)c).ToList() ?? [], ignoreOrder: true);
    }

    // ---- Claims ------------------------------------------------------------------------------------------

    /// <summary>The scenario's claim, submitted once (shared with the other scenario classes) and settled in <paramref name="expected"/>.</summary>
    private async Task<Guid> SettledClaimAsync(GoldenScenario scenario, ClaimStatus expected)
    {
        var claimId = await ScenarioClaims.SubmitOnceAsync(scenario, SubmitAsClaimantAsync);
        (await WaitUntilSettledAsync(claimId)).ShouldBe(expected, $"{scenario.ScenarioId} must settle as {expected}");
        return claimId;
    }

    /// <summary>The scenario's claim once its adjudication has settled in a status <paramref name="until"/> accepts.</summary>
    private async Task<Guid> SettledClaimAsync(GoldenScenario scenario, Func<ClaimStatus, bool> until)
    {
        var claimId = await ScenarioClaims.SubmitOnceAsync(scenario, SubmitAsClaimantAsync);
        until(await WaitUntilSettledAsync(claimId)).ShouldBeTrue();
        return claimId;
    }

    /// <summary>
    /// The <c>US6-override</c> claim after its reviewer's decision: escalated on its value, then rejected by the
    /// scenario's reviewer against the AI's approval. The decision is recorded once; a later call finds it decided.
    /// </summary>
    private async Task<Guid> DecidedOverrideClaimAsync(GoldenScenario scenario)
    {
        var claimId = await ScenarioClaims.SubmitOnceAsync(scenario, SubmitAsClaimantAsync);
        var status = await WaitUntilSettledAsync(claimId);
        if (status == ClaimStatus.UnderReview)
        {
            var reviewer = TestStaffUsers.Find(scenario.Reviewer!);
            using var client = fixture.CreateStaffClient(reviewer);
            using var detail = await client.GetAsync($"/api/claims/{claimId}", Ct);
            detail.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/review-decisions")
            {
                Content = JsonContent.Create(scenario.ReviewerDecision),
            };
            request.Headers.TryAddWithoutValidation("If-Match", detail.Headers.ETag.ShouldNotBeNull().Tag);

            using var decided = await client.SendAsync(request, Ct);

            decided.StatusCode.ShouldBe(HttpStatusCode.Created, await decided.Content.ReadAsStringAsync(Ct));
            status = await fixture.ClaimStatusAsync(claimId, Ct) ?? status;
        }

        status.ShouldBe(ClaimStatus.Rejected);
        return claimId;
    }

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

    // ---- API -----------------------------------------------------------------------------------------------

    /// <summary><c>GET /api/claims/{claimId}/trace</c> as <paramref name="user"/>, which must succeed.</summary>
    private async Task<Trace> TraceAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}/trace", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Trace(
            body.GetProperty("claimId").GetGuid(),
            body.GetProperty("integrity").GetProperty("hashChainValid").GetBoolean(),
            body.GetProperty("entries").EnumerateArray().Select(TraceEntry.From).ToList());
    }

    // ---- Database (as the owner: reads for assertions and the deliberate tampering) ------------------------

    private async Task<string> TrailSummaryAsync(Guid claimId, int seq)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select summary from audit.decision_trail_entries where claim_id = @id and seq = @seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("seq", seq);
        return (string)(await command.ExecuteScalarAsync(Ct)).ShouldNotBeNull();
    }

    /// <summary>
    /// Rewrites one entry's summary as the table owner can: the <c>append_only</c> trigger is disabled and
    /// re-enabled inside the same transaction, so no other session ever sees the table without it.
    /// </summary>
    private async Task<int> SetTrailSummaryAsync(Guid claimId, int seq, string summary)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var disable = new NpgsqlCommand("alter table audit.decision_trail_entries disable trigger append_only", connection, transaction))
        {
            await disable.ExecuteNonQueryAsync(Ct);
        }

        int updated;
        await using (var command = new NpgsqlCommand(
            "update audit.decision_trail_entries set summary = @summary where claim_id = @id and seq = @seq", connection, transaction))
        {
            command.Parameters.AddWithValue("summary", summary);
            command.Parameters.AddWithValue("id", claimId);
            command.Parameters.AddWithValue("seq", seq);
            updated = await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var enable = new NpgsqlCommand("alter table audit.decision_trail_entries enable trigger append_only", connection, transaction))
        {
            await enable.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
        return updated;
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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record Trace(Guid ClaimId, bool HashChainValid, List<TraceEntry> Entries)
    {
        public TraceEntry Single(string step)
            => Entries.SingleOrDefault(e => e.Step == step) ?? throw new ShouldAssertException($"The trace has no single {step} entry.");

        /// <summary>The entry right after the single <paramref name="step"/> entry.</summary>
        public TraceEntry EntryAfter(string step)
        {
            var index = Entries.IndexOf(Single(step));
            return index + 1 < Entries.Count ? Entries[index + 1] : throw new ShouldAssertException($"Nothing follows {step}.");
        }
    }

    /// <param name="Details">The step details, or an undefined element when the entry has none.</param>
    private sealed record TraceEntry(
        int Seq, DateTimeOffset OccurredAt, string Step, string Actor, string Summary, JsonElement Details,
        IReadOnlyList<JsonElement> AiCalls, IReadOnlyList<JsonElement> RagQueries)
    {
        public static TraceEntry From(JsonElement entry)
            => new(
                entry.GetProperty("seq").GetInt32(),
                entry.GetProperty("occurredAt").GetDateTimeOffset(),
                entry.GetProperty("step").GetString()!,
                entry.GetProperty("actor").GetString()!,
                entry.GetProperty("summary").GetString()!,
                entry.TryGetProperty("details", out var details) ? details : default,
                List(entry, "aiCalls"),
                List(entry, "ragQueries"));

        private static List<JsonElement> List(JsonElement entry, string name)
            => entry.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
    }
}
