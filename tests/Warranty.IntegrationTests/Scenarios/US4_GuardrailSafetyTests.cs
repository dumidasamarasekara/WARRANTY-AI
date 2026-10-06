using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// User story 4 end to end (quickstart S7, S8, S10, S18, S19, S22, S23 and the AI-failure and tool-scope
/// probes): whatever the replayed model recommends — even when it is deliberately wrong, manipulated,
/// unavailable, invalid or reaches for a consequential tool — the deterministic guardrails keep every unsafe
/// claim away from automatic finalization (FR-026 – FR-031, FR-037, SC-010). Each scenario's expected outcome
/// is labelled in seed/golden/scenarios.json and checked by <see cref="ShouldMatchExpectedOutcomeAsync"/>;
/// the tests add what each case is about. Every scenario has its own serial and evidence files, except the
/// photo S7-reuse shares on purpose with S7-reuse-original, and S4-aurora, whose claim is shared with US2.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class US4_GuardrailSafetyTests(WarrantyAppFixture fixture)
{
    /// <summary>Words and reference IDs that must never reach a claimant (FR-037).</summary>
    private static readonly string[] RiskVocabulary = ["risk", "fraud", "signal", "manipulation", "suspicious", "EV-", "POL-", "GLB-"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- S7 / S8: evidence and manipulation signals ------------------------------------------------

    [Theory]
    [InlineData("S7-serial", "SERIAL_MISMATCH_PHOTO")]
    [InlineData("S7-reuse", "EVIDENCE_REUSED")]
    public async Task S7_suspicious_evidence_goes_to_review_with_its_signal_hidden_from_the_claimant(string scenarioId, string signal)
    {
        var scenario = GoldenScenario.Load(scenarioId);

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        (await RiskSignalCodesAsync(outcome.RunId)).ShouldContain(signal);
        (await ClaimRowAsync(outcome.ClaimId)).FinalOutcome.ShouldBeNull();
        scenario.ClaimantViewHidesRisk.ShouldBeTrue();
    }

    [Fact]
    public async Task S7_reuse_flags_the_photo_of_an_earlier_claim_of_the_same_tenant_although_the_model_recommends_approval()
    {
        var scenario = GoldenScenario.Load("S7-reuse");
        scenario.FaultyRecommendation.ShouldBeTrue();

        var outcome = await AdjudicatedAsync(scenario);

        var original = await GoldenScenario.Load("S7-reuse-original").SubmitOnceAsync(SubmitAsClaimantAsync);
        (await CountAsync(
            "select count(*) from claims.claim_evidence where claim_id = @value and sha256 in (select sha256 from claims.claim_evidence where claim_id <> @value)",
            original)).ShouldBe(1, "exactly one photo of the earlier claim is submitted again");
        (await RecommendationDecisionAsync(outcome.RunId)).ShouldBe("APPROVE");
        (await DispositionAsync(outcome.RunId)).ShouldBe(Disposition.HumanReview);
    }

    [Theory]
    [InlineData("S8")]
    [InlineData("S8-invoice")]
    [InlineData("S8-photo")]
    public async Task S8_an_instruction_to_the_system_raises_MANIPULATION_ATTEMPT_and_is_never_auto_approved(string scenarioId)
    {
        var scenario = GoldenScenario.Load(scenarioId);

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        (await RiskSignalCodesAsync(outcome.RunId)).ShouldContain("MANIPULATION_ATTEMPT");
        (await GuardrailChecksAsync(outcome.RunId))["NO_MANIPULATION"].ShouldBeFalse();
        (await DispositionAsync(outcome.RunId)).ShouldNotBe(Disposition.AutoApprove);
        var claim = await ClaimRowAsync(outcome.ClaimId);
        claim.Status.ShouldBe(ClaimStatus.UnderReview);
        claim.FinalOutcome.ShouldBeNull();
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", outcome.ClaimId)).ShouldBe(0);
        scenario.ClaimantViewHidesRisk.ShouldBeTrue();
    }

    // ---- S10, refusal, truncation, invalid output: AI failures are outcomes -------------------------

    [Theory]
    [InlineData("S10")]
    [InlineData("US4-refusal")]
    [InlineData("US4-truncated", Skip = "Pending T095")]
    public async Task An_AI_step_that_does_not_complete_is_recorded_and_the_claim_goes_to_review(string scenarioId)
    {
        var scenario = GoldenScenario.Load(scenarioId);

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        scenario.FailedAiSteps.ShouldNotBeEmpty();
        (await TrailAsync(outcome.ClaimId)).Where(e => e.Step == "AiStepFailed").ShouldNotBeEmpty();
        (await FailureReasonAsync(outcome.RunId)).ShouldNotBeNullOrWhiteSpace();
        (await ValidRecommendationCountAsync(outcome.RunId)).ShouldBe(0);
        scenario.ExpectedClaimantStatus.ShouldBe("Under Review");
    }

    [Fact(Skip = "Pending T095")]
    public async Task Invalid_decision_output_gets_exactly_one_corrective_turn_before_the_claim_goes_to_review()
    {
        var scenario = GoldenScenario.Load("US4-invalid-output");
        scenario.InvalidOutputFixtures.ShouldBe(["decision-1.json", "decision-2.json"]);

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        (await ModelCallCountAsync(outcome.RunId, "decision")).ShouldBe(2, "the first call and exactly one corrective turn");
        (await CountAsync("select count(*) from adjudication.recommendations where run_id = @value and not is_valid", outcome.RunId)).ShouldBe(1);
        (await ValidRecommendationCountAsync(outcome.RunId)).ShouldBe(0);
    }

    // ---- Consequential tool requested by a model ------------------------------------------------------

    [Fact]
    public async Task A_model_request_for_a_consequential_tool_is_denied_with_TOOL_SCOPE_VIOLATION_and_creates_no_repair_request()
    {
        var scenario = GoldenScenario.Load("US4-consequential-tool");
        scenario.DeniedToolRequests.ShouldBe(["create_repair_request"]);

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        var denied = (await ToolCallsAsync(outcome.RunId)).Where(c => c.Tool == "create_repair_request").ShouldHaveSingleItem();
        denied.Agent.ShouldBe("decision");
        denied.Allowed.ShouldBeFalse();
        denied.DenialReason.ShouldBe("consequential_tool");
        denied.Status.ShouldBe("denied");
        var violation = (await SecurityEventsOfClaimAsync(outcome.ClaimId)).Where(e => e.Kind == "TOOL_SCOPE_VIOLATION").ShouldHaveSingleItem();
        violation.TenantId.ShouldBe(TestStaffUsers.AuroraTenantId);
        violation.Actor.ShouldBe("decision");
        violation.Target.ShouldBe("tool:create_repair_request");
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", outcome.ClaimId)).ShouldBe(0);
        scenario.ExpectedRepairRequestCreated.ShouldBe(false);
    }

    // ---- S18: duplicate serial window -----------------------------------------------------------------

    [Fact]
    public async Task S18a_a_serial_claimed_30_days_ago_goes_to_review_with_DUPLICATE_SERIAL_CLAIM_and_medium_risk()
    {
        var scenario = GoldenScenario.Load("S18a");

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        outcome.Status.ShouldBe(ClaimStatus.UnderReview);
        (await RiskSignalCodesAsync(outcome.RunId)).ShouldContain("DUPLICATE_SERIAL_CLAIM");
        (await RiskAsync(outcome.RunId)).Level.ShouldBe(RiskLevel.Medium);
    }

    [Fact]
    public async Task S18b_a_serial_claimed_120_days_ago_is_approved_without_a_duplicate_signal()
    {
        var scenario = GoldenScenario.Load("S18b");

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        outcome.Status.ShouldBe(ClaimStatus.Approved);
        (await RiskSignalCodesAsync(outcome.RunId)).ShouldNotContain("DUPLICATE_SERIAL_CLAIM");
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", outcome.ClaimId)).ShouldBe(1);
    }

    // ---- S19: the computed risk, not the model's ---------------------------------------------------------

    [Fact]
    public async Task S19_the_computed_risk_is_Medium_although_the_model_says_LOW_so_the_claim_goes_to_review()
    {
        var scenario = GoldenScenario.Load("S19");

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        var risk = await RiskAsync(outcome.RunId);
        risk.Level.ShouldBe(RiskLevel.Medium);
        risk.Score.ShouldBe(25);
        (await ModelRiskLevelAsync(outcome.RunId)).ShouldBe("LOW");
        (await GuardrailChecksAsync(outcome.RunId))["RISK_LOW"].ShouldBeFalse();
        (await RecommendationDecisionAsync(outcome.RunId)).ShouldBe("APPROVE");
    }

    // ---- S22: exclusion grounding, compared with S4-aurora ---------------------------------------------

    [Fact]
    public async Task S22_a_liquid_damage_rejection_without_liquid_damage_in_the_photos_goes_to_review_while_S4_aurora_is_still_rejected()
    {
        var scenario = GoldenScenario.Load("S22");

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        (await GuardrailChecksAsync(outcome.RunId))["GROUNDED_IN_CLAUSE"].ShouldBeFalse();
        (await RecommendationDecisionAsync(outcome.RunId)).ShouldBe("REJECT");
        (await ClaimRowAsync(outcome.ClaimId)).FinalOutcome.ShouldBeNull();

        // The same kind of model rejection, grounded in a photo damage type its exclusion lists, is still finalized.
        var aurora = GoldenScenario.Load("S4-aurora");
        var rejected = await AdjudicatedAsync(aurora);
        await ShouldMatchExpectedOutcomeAsync(aurora, rejected);
        rejected.Status.ShouldBe(ClaimStatus.Rejected);
        (await DispositionAsync(rejected.RunId)).ShouldBe(Disposition.AutoReject);
        (await GuardrailChecksAsync(rejected.RunId))["GROUNDED_IN_CLAUSE"].ShouldBeTrue();
    }

    // ---- S23: invoice consistency tolerances -------------------------------------------------------------

    [Fact]
    public async Task S23a_seller_and_price_within_the_matching_tolerances_are_approved_with_every_consistency_check_matching()
    {
        var scenario = GoldenScenario.Load("S23a");

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        outcome.Status.ShouldBe(ClaimStatus.Approved);
        var consistency = await InvoiceConsistencyAsync(outcome.RunId);
        consistency.ShouldNotBeEmpty();
        consistency.ShouldAllBe(c => c.Match);
        (await RiskSignalCodesAsync(outcome.RunId)).ShouldNotContain("SOURCE_INCONSISTENCY");
    }

    [Fact]
    public async Task S23b_a_price_outside_the_tolerance_goes_to_review_with_SOURCE_INCONSISTENCY_on_the_price()
    {
        var scenario = GoldenScenario.Load("S23b");

        var outcome = await AdjudicatedAsync(scenario);

        await ShouldMatchExpectedOutcomeAsync(scenario, outcome);
        outcome.Status.ShouldBe(ClaimStatus.UnderReview);
        (await InvoiceConsistencyAsync(outcome.RunId)).Where(c => !c.Match).Select(c => c.Field).ShouldBe(["purchasePrice"]);
        (await RiskSignalCodesAsync(outcome.RunId)).ShouldContain("SOURCE_INCONSISTENCY");
    }

    // ---- The labelled outcome of a scenario ----------------------------------------------------------

    /// <summary>Asserts everything the scenario's <c>expected</c> block states about its claim's latest run.</summary>
    private async Task ShouldMatchExpectedOutcomeAsync(GoldenScenario scenario, Adjudicated outcome)
    {
        var id = scenario.ScenarioId;
        var claim = await ClaimRowAsync(outcome.ClaimId);
        if (scenario.ExpectedStatus is { } status)
        {
            claim.Status.ShouldBe(Enum.Parse<ClaimStatus>(status), id);
        }

        if (scenario.Expected["decidedBy"] is { } decidedBy)
        {
            claim.FinalDecidedBy.ShouldBe(Enum.Parse<DecidedBy>((string)decidedBy!), id);
        }

        if (scenario.ExpectedDisposition is { } disposition)
        {
            (await DispositionAsync(outcome.RunId)).ShouldBe(Enum.Parse<Disposition>(disposition), id);
        }

        var reasons = await EscalationReasonsAsync(outcome.RunId);
        foreach (var reason in scenario.ExpectedEscalationReasons)
        {
            reasons.ShouldContain(reason, id);
        }

        var checks = await GuardrailChecksAsync(outcome.RunId);
        foreach (var check in scenario.FailedGuardrails)
        {
            checks[check].ShouldBeFalse($"{id}: {check} must fail");
        }

        foreach (var check in scenario.PassedGuardrails)
        {
            checks[check].ShouldBeTrue($"{id}: {check} must pass");
        }

        var signals = await RiskSignalCodesAsync(outcome.RunId);
        foreach (var signal in scenario.ExpectedRiskSignals)
        {
            signals.ShouldContain(signal, id);
        }

        foreach (var signal in scenario.AbsentRiskSignals)
        {
            signals.ShouldNotContain(signal, id);
        }

        var risk = await RiskAsync(outcome.RunId);
        if (scenario.ExpectedRiskLevel is { } level)
        {
            risk.Level.ShouldBe(Enum.Parse<RiskLevel>(level), id);
        }

        if (scenario.ExpectedRiskScore is { } score)
        {
            risk.Score.ShouldBe(score, id);
        }

        if (scenario.ExpectedModelRiskLevel is { } modelLevel)
        {
            (await ModelRiskLevelAsync(outcome.RunId)).ShouldBe(modelLevel, id);
        }

        if (scenario.HasExpectedRecommendation)
        {
            if (scenario.ExpectedRecommendation is { } decision)
            {
                (await RecommendationDecisionAsync(outcome.RunId)).ShouldBe(decision, id);
                (await CitedClauseKeysAsync(outcome.RunId)).ShouldBe(scenario.ExpectedClauseKeys, ignoreOrder: true, id);
            }
            else
            {
                (await ValidRecommendationCountAsync(outcome.RunId)).ShouldBe(0, id);
            }
        }

        if (scenario.ExpectedRecommendationValid is false)
        {
            (await CountAsync("select count(*) from adjudication.recommendations where run_id = @value and not is_valid", outcome.RunId))
                .ShouldBe(1, $"{id}: an invalid recommendation is stored");
        }

        var trail = await TrailAsync(outcome.ClaimId);
        if (scenario.FailedAiSteps.Count > 0)
        {
            trail.Where(e => e.Step == "AiStepFailed").Select(e => e.Actor).ShouldBe(scenario.FailedAiSteps, ignoreOrder: true, id);
            var failureReason = (await FailureReasonAsync(outcome.RunId)).ShouldNotBeNull(id);
            foreach (var agent in scenario.FailedAiSteps)
            {
                failureReason.Split('\n').ShouldContain(line => line.StartsWith($"{agent}: ", StringComparison.Ordinal), id);
            }
        }
        else
        {
            trail.Select(e => e.Step).ShouldNotContain("AiStepFailed", id);
        }

        foreach (var (agent, calls) in scenario.ExpectedModelCalls)
        {
            (await ModelCallCountAsync(outcome.RunId, agent)).ShouldBe(calls, $"{id}: model calls of {agent}");
        }

        if (scenario.ExpectedInvoiceMismatches is { } mismatches)
        {
            (await InvoiceConsistencyAsync(outcome.RunId)).Where(c => !c.Match).Select(c => c.Field).ShouldBe(mismatches, ignoreOrder: true, id);
        }

        if (scenario.ExpectedSecurityEvents.Count > 0)
        {
            (await SecurityEventsOfClaimAsync(outcome.ClaimId)).Select(e => e.Kind).ShouldBe(scenario.ExpectedSecurityEvents, ignoreOrder: true, id);
        }

        if (scenario.ExpectedRepairRequestCreated is false)
        {
            (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", outcome.ClaimId)).ShouldBe(0, id);
        }

        // The trail ends with the guardrail evaluation and its outcome; only executed actions may follow.
        var steps = trail.Select(e => e.Step).Where(s => s != "ActionExecuted").ToList();
        steps.TakeLast(scenario.ExpectedLastTrailEntries.Count).ShouldBe(scenario.ExpectedLastTrailEntries, id);

        if (scenario.ExpectedClaimantStatus is not null || scenario.ClaimantViewHidesRisk)
        {
            var view = await ClaimantViewAsync(scenario, claim.Reference, scenario.ContactEmail);
            if (scenario.ExpectedClaimantStatus is { } claimantStatus)
            {
                // The view carries the status code; "Under Review" is its claimant-facing text (ui-design.md).
                view.GetProperty("status").GetString().ShouldBe(claimantStatus.Replace(" ", string.Empty, StringComparison.Ordinal), id);
            }

            ShouldContainNoRiskData(view, id);
        }
    }

    // ---- Submission ----------------------------------------------------------------------------------

    /// <summary>
    /// The scenario's claim, submitted once per test run after the claims it depends on (<c>submitAfter</c>)
    /// have settled, and its latest run once the claim has settled.
    /// </summary>
    private async Task<Adjudicated> AdjudicatedAsync(GoldenScenario scenario)
    {
        foreach (var earlier in scenario.SubmitAfter.Select(GoldenScenario.Load))
        {
            var earlierId = await earlier.SubmitOnceAsync(SubmitAsClaimantAsync);
            (await WaitUntilSettledAsync(earlierId)).ShouldBe(Enum.Parse<ClaimStatus>(earlier.ExpectedStatus!), earlier.ScenarioId);
        }

        var claimId = await scenario.SubmitOnceAsync(SubmitAsClaimantAsync);
        var status = await WaitUntilSettledAsync(claimId);
        var runId = await ScalarAsync<Guid>(
            "select id from adjudication.adjudication_runs where claim_id = @value order by round desc limit 1", claimId);
        return new Adjudicated(claimId, status, runId);
    }

    /// <summary>Submits through the tenant's claimant channel; the public response carries no claim ID, so it is looked up by reference.</summary>
    private async Task<Guid> SubmitAsClaimantAsync(GoldenScenario scenario)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsync("/api/public/claims", scenario.ToSubmission(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, scenario.ScenarioId);
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
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
    private static void ShouldContainNoRiskData(JsonElement view, string scenarioId)
    {
        foreach (var property in view.EnumerateObject())
        {
            property.Name.ShouldNotContain("risk", Case.Insensitive, scenarioId);
            property.Name.ShouldNotContain("signal", Case.Insensitive, scenarioId);
            property.Name.ShouldNotContain("guardrail", Case.Insensitive, scenarioId);
        }

        var text = view.GetRawText();
        foreach (var term in RiskVocabulary)
        {
            text.ShouldNotContain(term, Case.Insensitive, scenarioId);
        }
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<ClaimRow> ClaimRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select reference, status, final_outcome, final_decided_by from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} not found");
        return new ClaimRow(
            reader.GetString(0),
            WireName.Parse<ClaimStatus>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : WireName.Parse<FinalOutcome>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : WireName.Parse<DecidedBy>(reader.GetString(3)));
    }

    private async Task<Disposition?> DispositionAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select disposition from adjudication.adjudication_runs where id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        return await command.ExecuteScalarAsync(Ct) is string disposition ? WireName.Parse<Disposition>(disposition) : null;
    }

    private async Task<string?> FailureReasonAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select failure_reason from adjudication.adjudication_runs where id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    /// <summary>The decision of the run's valid recommendation.</summary>
    private Task<string> RecommendationDecisionAsync(Guid runId)
        => ScalarAsync<string>("select decision from adjudication.recommendations where run_id = @value and is_valid", runId);

    private Task<long> ValidRecommendationCountAsync(Guid runId)
        => CountAsync("select count(*) from adjudication.recommendations where run_id = @value and is_valid", runId);

    /// <summary>The risk level the model reported in its decision output (<c>risk.level</c>), kept in the raw output.</summary>
    private Task<string> ModelRiskLevelAsync(Guid runId)
        => ScalarAsync<string>("select raw_output #>> '{risk,level}' from adjudication.recommendations where run_id = @value", runId);

    /// <summary>
    /// The clauses the valid recommendation cites as <c>SUPPORTS_COVERAGE</c>, <c>SUPPORTS_REJECTION</c> or
    /// <c>DEFINES_PERIOD</c> — what <c>citedClauseKeys</c> labels; <c>CONTEXT</c> citations are left out.
    /// </summary>
    private async Task<List<string>> CitedClauseKeysAsync(Guid runId)
    {
        var keys = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select r.clause_key
            from adjudication.recommendations rec
            cross join jsonb_array_elements(rec.policy_refs) p
            join adjudication.retrieved_policy_refs r on r.run_id = rec.run_id and r.ref_id = p ->> 'ref'
            where rec.run_id = @id and rec.is_valid and p ->> 'relevance' in ('SUPPORTS_COVERAGE', 'SUPPORTS_REJECTION', 'DEFINES_PERIOD')
            """, connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private async Task<RiskRow> RiskAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select level, score from adjudication.risk_assessments where run_id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"run {runId} has no risk assessment");
        return new RiskRow(WireName.Parse<RiskLevel>(reader.GetString(0)), reader.GetInt32(1));
    }

    /// <summary>The codes of the run's risk signals (deterministic and AI-reported).</summary>
    private async Task<List<string>> RiskSignalCodesAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select signals::text from adjudication.risk_assessments where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(s => s.GetProperty("code").GetString()!).ToList();
    }

    /// <summary>Guardrail check code → passed, from the run's ordered <c>checks</c>.</summary>
    private async Task<Dictionary<string, bool>> GuardrailChecksAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select checks::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("passed").GetBoolean());
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

    /// <summary>The consistency checks of the run's invoice extraction (field, match).</summary>
    private async Task<List<ConsistencyRow>> InvoiceConsistencyAsync(Guid runId)
    {
        var json = await ScalarAsync<string>(
            "select consistency::text from adjudication.evidence_findings where run_id = @value and kind = 'InvoiceExtraction'", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(c => new ConsistencyRow(c.GetProperty("field").GetString()!, c.GetProperty("match").GetBoolean()))
            .ToList();
    }

    /// <summary>The model calls an agent made in the run, including corrective turns and retries.</summary>
    private async Task<long> ModelCallCountAsync(Guid runId, string agent)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from aiops.model_calls where run_id = @run and agent = @agent", connection);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("agent", agent);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<List<ToolCallRow>> ToolCallsAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select agent, tool, allowed, denial_reason, status from aiops.tool_calls where run_id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<ToolCallRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new ToolCallRow(
                reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>Security events whose details name the claim.</summary>
    private async Task<List<SecurityEventRow>> SecurityEventsOfClaimAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select tenant_id, kind, actor, target from audit.security_events where details::text ilike '%' || @claim || '%'", connection);
        command.Parameters.AddWithValue("claim", claimId.ToString());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<SecurityEventRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new SecurityEventRow(
                reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>The claim's decision trail in order.</summary>
    private async Task<List<TrailRow>> TrailAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select step, actor from audit.decision_trail_entries where claim_id = @id order by seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<TrailRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new TrailRow(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
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

    /// <summary>A scenario's claim once settled, with its latest adjudication run.</summary>
    private sealed record Adjudicated(Guid ClaimId, ClaimStatus Status, Guid RunId);

    private sealed record ClaimRow(string Reference, ClaimStatus Status, FinalOutcome? FinalOutcome, DecidedBy? FinalDecidedBy);

    private sealed record RiskRow(RiskLevel Level, int Score);

    private sealed record ConsistencyRow(string Field, bool Match);

    private sealed record ToolCallRow(string Agent, string Tool, bool Allowed, string? DenialReason, string Status);

    private sealed record SecurityEventRow(Guid? TenantId, string Kind, string Actor, string? Target);

    private sealed record TrailRow(string Step, string Actor);
}
