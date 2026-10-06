using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// User story 3 end to end (quickstart S5, S11, S13, S20 and a claim without a valid recommendation):
/// escalated claims reach the review queue of their own tenant only, the claim detail shows a reviewer
/// everything FR-033 lists (and a claims agent only what FR-005 allows), and reviewer decisions are
/// validated, recorded next to the unchanged AI recommendation and refused for the claim's own
/// submitter (FR-034 – FR-036, research R29). Claims are adjudicated with recorded AI responses
/// (seed/golden/scenarios.json, tests/fixtures/ai-recordings/).
/// <para>
/// A decision changes its claim for every later test, and xunit does not order the tests of a class.
/// So each escalated claim is observed once, before anything decides it (<see cref="EscalatedAsync"/>:
/// status, review queues, staff details, the recommendation row), and every test that decides a claim
/// awaits that observation first; the read-only tests assert on the observation.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class US3_HumanReviewTests(WarrantyAppFixture fixture)
{
    private const string PendingDecisionEndpoint = "Pending T088";

    /// <summary>Guardrail reasons hidden from claims agents behind one label (data-model.md, FR-005).</summary>
    private static readonly string[] RiskRelatedReasons = ["RISK_MEDIUM", "RISK_HIGH", "EVIDENCE_CONFLICT", "AI_DETERMINISTIC_DISAGREEMENT", "UNSAFE_CLAIMANT_TEXT"];

    /// <summary>Words and reference IDs that must never reach a claimant (FR-037).</summary>
    private static readonly string[] RiskVocabulary = ["risk", "fraud", "signal", "manipulation", "suspicious", "EV-", "POL-", "GLB-"];

    /// <summary>Claims submitted once per scenario and shared by the tests that read them.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<Guid>>> SubmittedClaims = new(StringComparer.Ordinal);

    /// <summary>Each escalated claim as observed before any decision, by scenario.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<Escalation>>> Escalations = new(StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- S5: escalation on value, queue, detail -------------------------------------------------

    [Fact]
    public async Task S5_escalates_on_claim_value_and_is_queued_for_Aurora_reviewers_only()
    {
        var scenario = GoldenScenario.Load("S5");

        var escalation = await EscalatedAsync(scenario);

        escalation.Status.ShouldBe(ClaimStatus.UnderReview);
        var run = await LatestRunAsync(escalation.ClaimId);
        run.Disposition.ShouldBe(Disposition.HumanReview);
        (await EscalationReasonsAsync(run.Id)).ShouldBe(scenario.ExpectedEscalationReasons, ignoreOrder: true);

        var item = escalation.QueueItem(TestStaffUsers.ReviewerAurora.Username);
        item.GetProperty("escalationReasons").EnumerateArray().Select(r => r.GetString()).ShouldContain("claim value above auto-approval limit");
        item.GetProperty("claimValue").GetDecimal().ShouldBe(1400m);
        item.GetProperty("aiDecision").GetString().ShouldBe("APPROVE");
        item.GetProperty("riskLevel").GetString().ShouldBe("Low");
        item.GetProperty("submittedByMe").GetBoolean().ShouldBeFalse();
        scenario.ReviewQueues.ShouldContain(TestStaffUsers.ReviewerAurora.Username);

        foreach (var username in scenario.AbsentFromReviewQueues)
        {
            escalation.Queues[username].ShouldBeNull($"the claim must not be in {username}'s review queue");
        }
    }

    [Fact]
    public async Task S5_claim_detail_shows_a_reviewer_every_FR_033_section_with_an_etag()
    {
        var scenario = GoldenScenario.Load("S5");

        var escalation = await EscalatedAsync(scenario);

        var reviewer = escalation.ReviewerDetail;
        reviewer.Status.ShouldBe(HttpStatusCode.OK);
        reviewer.ETag.ShouldNotBeNull();
        reviewer.ETag.ShouldMatch("^\"[0-9]+\"$");
        var detail = reviewer.Body;

        // Claim data.
        detail.GetProperty("claimId").GetGuid().ShouldBe(escalation.ClaimId);
        detail.GetProperty("status").GetString().ShouldBe("UnderReview");
        detail.GetProperty("customer").GetProperty("email").GetString().ShouldBe(scenario.ContactEmail);
        var product = detail.GetProperty("product");
        product.GetProperty("serialNumber").GetString().ShouldBe(scenario.Serial);
        product.GetProperty("inCatalog").GetBoolean().ShouldBeTrue();
        product.GetProperty("claimValue").GetDecimal().ShouldBe(1400m);
        detail.GetProperty("purchase").GetProperty("price").GetDecimal().ShouldBe(1400m);
        detail.GetProperty("problemDescription").GetString().ShouldNotBeNullOrWhiteSpace();

        // All evidence, invoice and photos, with the references the evaluation used.
        var evidence = detail.GetProperty("evidence").EnumerateArray().ToList();
        evidence.Select(e => e.GetProperty("kind").GetString()).ShouldBe(["Invoice", "Photo", "Photo"], ignoreOrder: true);
        evidence.Select(e => e.GetProperty("ref").GetString()).ShouldBe(["EV-1", "EV-2", "EV-3"], ignoreOrder: true);

        var evaluation = detail.GetProperty("latestEvaluation");

        // Extracted data and evidence findings.
        evaluation.GetProperty("extraction").ValueKind.ShouldBe(JsonValueKind.Object);
        evaluation.GetProperty("evidenceFindings").GetArrayLength().ShouldBe(evidence.Count);

        // Retrieved policy excerpts with references, versions and dates.
        var references = evaluation.GetProperty("policyReferences").EnumerateArray().ToList();
        references.ShouldNotBeEmpty();
        references.ShouldAllBe(r => r.GetProperty("ref").GetString()!.StartsWith("POL-", StringComparison.Ordinal)
                                    && r.GetProperty("version").GetInt32() > 0
                                    && !string.IsNullOrWhiteSpace(r.GetProperty("effectiveFrom").GetString())
                                    && !string.IsNullOrWhiteSpace(r.GetProperty("excerpt").GetString()));
        references.Where(r => r.GetProperty("cited").GetBoolean()).Select(r => r.GetProperty("clauseKey").GetString())
            .ShouldBe(scenario.ExpectedClauseKeys, ignoreOrder: true);
        evaluation.GetProperty("policyAssessment").ValueKind.ShouldBe(JsonValueKind.Object);

        // AI recommendation, confidence and reasoning summary.
        var recommendation = evaluation.GetProperty("recommendation");
        recommendation.GetProperty("isValid").GetBoolean().ShouldBeTrue();
        recommendation.GetProperty("decision").GetString().ShouldBe("APPROVE");
        recommendation.GetProperty("confidence").GetInt32().ShouldBeInRange(1, 100);
        recommendation.GetProperty("reasoningSummary").GetString().ShouldNotBeNullOrWhiteSpace();

        // Risk level and signals.
        var risk = evaluation.GetProperty("risk");
        risk.GetProperty("level").GetString().ShouldBe("Low");
        risk.GetProperty("signals").ValueKind.ShouldBe(JsonValueKind.Array);

        // Guardrail results.
        var guardrails = evaluation.GetProperty("guardrails");
        guardrails.GetProperty("disposition").GetString().ShouldBe("HumanReview");
        guardrails.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ShouldContain("VALUE_ABOVE_LIMIT");
        var checks = guardrails.GetProperty("checks").EnumerateArray().ToList();
        checks.Single(c => c.GetProperty("code").GetString() == "CLAIM_VALUE_WITHIN_LIMIT").GetProperty("passed").GetBoolean().ShouldBeFalse();

        detail.GetProperty("reviewDecisions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_claims_agent_gets_the_S5_detail_without_risk_reasoning_or_guardrail_checks()
    {
        var escalation = await EscalatedAsync(GoldenScenario.Load("S5"));

        var agent = escalation.AgentDetail;
        agent.Status.ShouldBe(HttpStatusCode.OK);
        var evaluation = agent.Body.GetProperty("latestEvaluation");
        evaluation.TryGetProperty("risk", out _).ShouldBeFalse();
        evaluation.GetProperty("recommendation").TryGetProperty("reasoningSummary", out _).ShouldBeFalse();
        var guardrails = evaluation.GetProperty("guardrails");
        guardrails.TryGetProperty("checks", out _).ShouldBeFalse();

        // The reasons a reviewer sees, with every risk-related one shown only as "Additional checks required" (FR-005).
        var reviewerReasons = escalation.ReviewerDetail.Body.GetProperty("latestEvaluation").GetProperty("guardrails").GetProperty("reasons")
            .EnumerateArray().Select(r => r.GetString()!).ToList();
        var agentReasons = guardrails.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToList();
        agentReasons.ShouldBe(AsSeenByAgents(reviewerReasons));
        agentReasons.ShouldNotContain(r => RiskRelatedReasons.Contains(r));
    }

    // ---- S11: the reviewer overrides the AI on the S5 claim --------------------------------------

    [Fact(Skip = PendingDecisionEndpoint)]
    public async Task S11_a_reviewer_rejects_the_S5_claim_against_the_AI_with_a_justification_and_a_claimant_explanation()
    {
        var scenario = GoldenScenario.Load("S11");
        var escalation = await EscalatedAsync(GoldenScenario.Load(scenario.ClaimOf!));
        var claimId = escalation.ClaimId;
        var reviewer = TestStaffUsers.Find(scenario.Reviewer!);
        var etag = await CurrentETagAsync(reviewer, claimId);
        var decision = scenario.ReviewerDecision;
        var justification = (string)decision["justification"]!;
        var claimantExplanation = (string)decision["claimantExplanation"]!;

        // Invalid decisions change nothing.
        (await DecideAsync(reviewer, claimId, etag, Without(decision, "justification"))).ShouldBeValidationProblem("justification");
        (await DecideAsync(reviewer, claimId, etag, Without(decision, "claimantExplanation"))).ShouldBeValidationProblem("claimantExplanation");
        foreach (var term in new[] { "fraud", "POL-1" })
        {
            var unsafeText = With(decision, "claimantExplanation", $"We looked at your claim and found a {term} concern, so it cannot be covered.");
            var problem = await DecideAsync(reviewer, claimId, etag, unsafeText);
            problem.ShouldBeValidationProblem("claimantExplanation");
            problem.Body.ShouldContain(term, Case.Insensitive);
        }

        var requestWithExplanation = new JsonObject
        {
            ["decision"] = "RequestInformation",
            ["justification"] = justification,
            ["claimantExplanation"] = claimantExplanation,
            ["requestedItems"] = new JsonArray(new JsonObject { ["item"] = "PHOTO_OF_DAMAGE", ["reason"] = "A close-up photo of the display is needed." }),
        };
        (await DecideAsync(reviewer, claimId, etag, requestWithExplanation)).ShouldBeValidationProblem("claimantExplanation");
        (await ClaimRowAsync(claimId)).Status.ShouldBe(ClaimStatus.UnderReview);
        (await ReviewDecisionRowsAsync(claimId)).ShouldBeEmpty();

        // The valid rejection.
        var recorded = await DecideAsync(reviewer, claimId, etag, decision);

        recorded.Status.ShouldBe(HttpStatusCode.Created, recorded.Body);
        var body = recorded.Json;
        body.GetProperty("decision").GetString().ShouldBe("Reject");
        body.GetProperty("overridesAi").GetBoolean().ShouldBe(scenario.ExpectedOverridesAi ?? true);
        body.GetProperty("reviewerName").GetString().ShouldNotBeNullOrWhiteSpace();

        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe(ClaimStatus.Rejected);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Rejected);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.Reviewer);
        claim.FinalExplanation.ShouldBe(claimantExplanation);
        claim.FinalizedAt.ShouldNotBeNull();

        var row = (await ReviewDecisionRowsAsync(claimId)).ShouldHaveSingleItem();
        row.Decision.ShouldBe("Reject");
        row.OverridesAi.ShouldBeTrue();
        row.ReviewerSub.ShouldBe(reviewer.Subject);
        row.Justification.ShouldBe(justification);
        row.ClaimantExplanation.ShouldBe(claimantExplanation);

        // The AI recommendation is kept as it was (FR-036).
        (await RecommendationRowAsync(escalation.RunId)).ShouldBe(escalation.RecommendationRow);
        (await RecommendationDecisionAsync(escalation.RunId)).ShouldBe("APPROVE");

        // The trail names the reviewer and keeps the justification next to the claimant explanation.
        var trail = await TrailAsync(claimId);
        var decided = trail.Single(e => e.Step == "ReviewerDecided");
        trail.Skip(trail.IndexOf(decided) + 1).ShouldAllBe(e => e.Step == "ActionExecuted", "only the executed actions follow the decision");
        decided.Actor.ShouldBe(reviewer.Subject);
        decided.Payload.ShouldContain(justification);
        decided.Payload.ShouldContain(claimantExplanation);

        // The claimant reads the reviewer's explanation, never the justification.
        var view = await ClaimantViewAsync(scenario, claim.Reference, scenario.ContactEmail);
        view.GetProperty("status").GetString().ShouldBe("Rejected");
        view.GetProperty("outcomeExplanation").GetString().ShouldBe(claimantExplanation);
        ShouldContainNoRiskData(view);
        view.GetRawText().ShouldNotContain(justification);

        // A claims agent sees the decision without its justification (FR-005); a reviewer sees both.
        var agentDetail = await DetailAsync(TestStaffUsers.AgentAurora, claimId);
        var agentDecision = agentDetail.Body.GetProperty("reviewDecisions").EnumerateArray().ShouldHaveSingleItem();
        agentDecision.TryGetProperty("justification", out _).ShouldBeFalse();
        agentDetail.Body.GetRawText().ShouldNotContain(justification);
        var reviewerDetail = await DetailAsync(reviewer, claimId);
        reviewerDetail.Body.GetProperty("reviewDecisions").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("justification").GetString().ShouldBe(justification);

        // A second decision: the precondition is checked first, so a stale If-Match is 412; with the current one the decided claim is 409.
        var stale = await DecideAsync(reviewer, claimId, etag, decision);
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed, stale.Body);
        stale.MediaType.ShouldBe("application/problem+json");
        var conflict = await DecideAsync(reviewer, claimId, reviewerDetail.ETag, decision);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict, conflict.Body);
        conflict.MediaType.ShouldBe("application/problem+json");
        (await ReviewDecisionRowsAsync(claimId)).Count.ShouldBe(1);
        (await ClaimRowAsync(claimId)).Status.ShouldBe(ClaimStatus.Rejected);
    }

    // ---- S13: always-review category, reviewer request for information ---------------------------

    [Fact]
    public async Task S13_escalates_for_the_always_review_category_and_the_claim_value_in_Borealis_only()
    {
        var scenario = GoldenScenario.Load("S13");

        var escalation = await EscalatedAsync(scenario);

        escalation.Status.ShouldBe(ClaimStatus.UnderReview);
        var reasons = await EscalationReasonsAsync(escalation.RunId);
        reasons.ShouldContain("ALWAYS_REVIEW_CATEGORY");
        reasons.ShouldContain("VALUE_ABOVE_LIMIT");
        reasons.ShouldBe(scenario.ExpectedEscalationReasons, ignoreOrder: true);

        var labels = escalation.QueueItem(TestStaffUsers.ReviewerBorealis.Username).GetProperty("escalationReasons")
            .EnumerateArray().Select(r => r.GetString()).ToList();
        labels.ShouldContain("product category always requires a human decision");
        labels.ShouldContain("claim value above auto-approval limit");
        foreach (var username in scenario.AbsentFromReviewQueues)
        {
            escalation.Queues[username].ShouldBeNull($"the claim must not be in {username}'s review queue");
        }
    }

    [Fact(Skip = PendingDecisionEndpoint)]
    public async Task S13_a_reviewer_request_for_information_moves_the_claim_to_PendingInformation()
    {
        var escalation = await EscalatedAsync(GoldenScenario.Load("S13"));
        var claimId = escalation.ClaimId;
        var reviewer = TestStaffUsers.ReviewerBorealis;
        var before = await ClaimRowAsync(claimId);
        var items = new JsonArray(
            new JsonObject { ["item"] = "PHOTO_OF_SERIAL_LABEL", ["reason"] = "Please send a photo of the rating plate inside the oven door." });
        var request = new JsonObject
        {
            ["decision"] = "RequestInformation",
            ["justification"] = "The serial label is not visible in the photos; it is needed before deciding a major appliance claim.",
            ["requestedItems"] = items,
        };

        var recorded = await DecideAsync(reviewer, claimId, await CurrentETagAsync(reviewer, claimId), request);

        recorded.Status.ShouldBe(HttpStatusCode.Created, recorded.Body);
        recorded.Json.GetProperty("decision").GetString().ShouldBe("RequestInformation");
        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe(ClaimStatus.PendingInformation);
        claim.ReviewerInfoRequested.ShouldBeTrue();
        claim.AutoInfoRequestCount.ShouldBe(before.AutoInfoRequestCount);
        claim.FinalOutcome.ShouldBeNull();

        var detail = (await DetailAsync(reviewer, claimId)).Body;
        detail.GetProperty("status").GetString().ShouldBe("PendingInformation");
        detail.GetProperty("reviewerInfoRequested").GetBoolean().ShouldBeTrue();
        detail.GetProperty("autoInfoRequestCount").GetInt32().ShouldBe(before.AutoInfoRequestCount);
        detail.GetProperty("requestedItems").EnumerateArray().Select(i => i.GetProperty("item").GetString()).ShouldBe(["PHOTO_OF_SERIAL_LABEL"]);
        (await TrailAsync(claimId)).Select(e => e.Step).ShouldContain("ReviewerDecided");
    }

    // ---- A claim without a valid recommendation ----------------------------------------------------

    [Fact]
    public async Task A_claim_whose_decision_call_fails_is_escalated_without_a_valid_recommendation()
    {
        var scenario = GoldenScenario.Load("US3-no-recommendation");

        var escalation = await EscalatedAsync(scenario);

        escalation.Status.ShouldBe(ClaimStatus.UnderReview);
        (await ValidRecommendationCountAsync(escalation.RunId)).ShouldBe(0);
        var reasons = await EscalationReasonsAsync(escalation.RunId);
        foreach (var reason in scenario.ExpectedEscalationReasons)
        {
            reasons.ShouldContain(reason);
        }

        var item = escalation.QueueItem(TestStaffUsers.ReviewerAurora.Username);
        item.TryGetProperty("aiDecision", out _).ShouldBeFalse("there is no valid AI decision to show");
    }

    [Fact(Skip = PendingDecisionEndpoint)]
    public async Task Without_a_valid_recommendation_an_approval_needs_no_justification_but_a_rejection_does()
    {
        var scenario = GoldenScenario.Load("US3-no-recommendation");
        var escalation = await EscalatedAsync(scenario);
        var claimId = escalation.ClaimId;
        var reviewer = TestStaffUsers.Find(scenario.Reviewer!);
        var etag = await CurrentETagAsync(reviewer, claimId);
        var approval = scenario.ReviewerDecision;
        approval.ContainsKey("justification").ShouldBeFalse();

        // Rejecting always needs a justification, even with nothing to override.
        (await DecideAsync(reviewer, claimId, etag, With(approval, "decision", "Reject"))).ShouldBeValidationProblem("justification");
        (await ClaimRowAsync(claimId)).Status.ShouldBe(ClaimStatus.UnderReview);

        var recorded = await DecideAsync(reviewer, claimId, etag, approval);

        recorded.Status.ShouldBe(HttpStatusCode.Created, recorded.Body);
        recorded.Json.GetProperty("overridesAi").GetBoolean().ShouldBe(scenario.ExpectedOverridesAi ?? false);
        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.Reviewer);
        claim.FinalExplanation.ShouldBe((string)approval["claimantExplanation"]!);
        var row = (await ReviewDecisionRowsAsync(claimId)).ShouldHaveSingleItem();
        row.OverridesAi.ShouldBeFalse();
        row.Justification.ShouldBeNull();

        // The approval is executed through the simulated integrations.
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(1);
    }

    // ---- S20: separation of duties -------------------------------------------------------------------

    [Fact]
    public async Task S20_the_review_queue_marks_the_claim_for_its_submitter_and_lists_it_for_other_reviewers()
    {
        var scenario = GoldenScenario.Load("S20");

        var escalation = await EscalatedAsync(scenario);

        escalation.Status.ShouldBe(ClaimStatus.UnderReview);
        (await SubmittedByAsync(escalation.ClaimId)).ShouldBe(TestStaffUsers.Find(scenario.SubmittedBy).Subject);
        var submitter = scenario.SelfReviewRefusedFor!;
        scenario.ReviewQueues.ShouldContain(submitter);
        foreach (var username in scenario.ReviewQueues)
        {
            escalation.QueueItem(username).GetProperty("submittedByMe").GetBoolean().ShouldBe(username == submitter, username);
        }
    }

    [Fact(Skip = PendingDecisionEndpoint)]
    public async Task S20_the_submitter_may_not_decide_the_claim_but_another_reviewer_may()
    {
        var scenario = GoldenScenario.Load("S20");
        var escalation = await EscalatedAsync(scenario);
        var claimId = escalation.ClaimId;
        var submitter = TestStaffUsers.Find(scenario.SelfReviewRefusedFor!);
        var approval = new JsonObject
        {
            ["decision"] = "Approve",
            ["claimantExplanation"] = "Your Aurora Book 15 is covered by the warranty; we are arranging a free repair at an authorized service center.",
        };
        var before = (await SecurityEventsAsync()).Select(e => e.Id).ToHashSet();

        var refused = await DecideAsync(submitter, claimId, await CurrentETagAsync(submitter, claimId), approval);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.Body);
        refused.MediaType.ShouldBe("application/problem+json");
        refused.Body.ShouldContain("another reviewer", Case.Insensitive);
        var written = (await SecurityEventsAsync()).Where(e => !before.Contains(e.Id)).ToList();
        foreach (var kind in scenario.ExpectedSecurityEvents)
        {
            var refusal = written.Where(e => e.Kind == kind).ShouldHaveSingleItem(kind);
            refusal.TenantId.ShouldBe(TestStaffUsers.AuroraTenantId);
            refusal.Target.ShouldNotBeNull().ShouldContain(claimId.ToString(), Case.Insensitive);
        }

        (await ClaimRowAsync(claimId)).Status.ShouldBe(ClaimStatus.UnderReview);
        (await ReviewDecisionRowsAsync(claimId)).ShouldBeEmpty();
        (await TrailAsync(claimId)).Select(e => e.Step).ShouldNotContain("ReviewerDecided");

        // Another reviewer of the tenant decides it.
        var reviewer = TestStaffUsers.ReviewerAurora;
        var recorded = await DecideAsync(reviewer, claimId, await CurrentETagAsync(reviewer, claimId), approval);

        recorded.Status.ShouldBe(HttpStatusCode.Created, recorded.Body);
        recorded.Json.GetProperty("overridesAi").GetBoolean().ShouldBeFalse();
        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.Reviewer);
        (await ReviewDecisionRowsAsync(claimId)).ShouldHaveSingleItem().ReviewerSub.ShouldBe(reviewer.Subject);
    }

    // ---- Observation of escalated claims ---------------------------------------------------------

    /// <summary>
    /// The scenario's claim (or the claim of its <c>claimOf</c> scenario) as first seen in review: it is
    /// submitted once, adjudicated and then observed exactly once — before any test decides it.
    /// </summary>
    private Task<Escalation> EscalatedAsync(GoldenScenario scenario)
    {
        var owner = scenario.ClaimOf is { } claimOf ? GoldenScenario.Load(claimOf) : scenario;
        return Escalations.GetOrAdd(owner.ScenarioId, _ => new Lazy<Task<Escalation>>(() => ObserveAsync(owner))).Value;
    }

    private async Task<Escalation> ObserveAsync(GoldenScenario scenario)
    {
        var claimId = await SubmitOnceAsync(scenario);
        var status = await WaitUntilSettledAsync(claimId);
        status.ShouldBe(ClaimStatus.UnderReview, $"{scenario.ScenarioId} must be escalated before it can be reviewed");

        var queues = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var username in scenario.ReviewQueues.Concat(scenario.AbsentFromReviewQueues).Append($"reviewer.{scenario.Tenant}").Distinct())
        {
            var queue = await ReviewQueueAsync(TestStaffUsers.Find(username));
            queues[username] = queue.Where(i => i.GetProperty("claimId").GetGuid() == claimId).Cast<JsonElement?>().SingleOrDefault();
        }

        var run = await LatestRunAsync(claimId);
        return new Escalation(
            claimId,
            status,
            run.Id,
            queues,
            await DetailAsync(TestStaffUsers.Find($"reviewer.{scenario.Tenant}"), claimId),
            await DetailAsync(TestStaffUsers.Find($"agent.{scenario.Tenant}"), claimId),
            await RecommendationRowAsync(run.Id));
    }

    /// <summary>Submits the scenario's claim once per test run (through its channel) and returns the claim ID.</summary>
    private Task<Guid> SubmitOnceAsync(GoldenScenario scenario)
        => SubmittedClaims.GetOrAdd(
            scenario.ScenarioId,
            _ => new Lazy<Task<Guid>>(() => scenario.IsAgentChannel ? SubmitAsStaffAsync(scenario) : SubmitAsClaimantAsync(scenario))).Value;

    /// <summary>Submits through the tenant's claimant channel; the public response carries no claim ID, so it is looked up by reference.</summary>
    private async Task<Guid> SubmitAsClaimantAsync(GoldenScenario scenario)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsync("/api/public/claims", scenario.ToSubmission(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
        return await ScalarAsync<Guid>("select id from claims.claims where reference = @value", reference);
    }

    /// <summary>Submits through <c>POST /api/claims</c> as the scenario's submitting staff user.</summary>
    private async Task<Guid> SubmitAsStaffAsync(GoldenScenario scenario)
    {
        using var client = fixture.CreateStaffClient(scenario.SubmittedBy);
        using var response = await client.PostAsync("/api/claims", scenario.ToSubmission(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("claimId").GetGuid();
    }

    private Task<ClaimStatus> WaitUntilSettledAsync(Guid claimId)
        => fixture.WaitForClaimStatusAsync(
            claimId, status => status is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation), TimeSpan.FromSeconds(90));

    // ---- API helpers ---------------------------------------------------------------------------------

    private async Task<List<JsonElement>> ReviewQueueAsync(TestStaffUser user)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync("/api/review-queue", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();
    }

    /// <summary><c>GET /api/claims/{claimId}</c> as <paramref name="user"/>: status, ETag and body (default when not 200).</summary>
    private async Task<DetailResponse> DetailAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>(Ct) : default;
        return new DetailResponse(response.StatusCode, response.Headers.ETag?.Tag, body);
    }

    /// <summary>The claim's current ETag, required as <c>If-Match</c> for a decision.</summary>
    private async Task<string> CurrentETagAsync(TestStaffUser user, Guid claimId)
    {
        var detail = await DetailAsync(user, claimId);
        detail.Status.ShouldBe(HttpStatusCode.OK);
        return detail.ETag.ShouldNotBeNull();
    }

    /// <summary><c>POST /api/claims/{claimId}/review-decisions</c> as <paramref name="user"/>, with <c>If-Match</c>.</summary>
    private async Task<DecisionResponse> DecideAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject decision)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/review-decisions")
        {
            Content = JsonContent.Create(decision),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await client.SendAsync(request, Ct);
        return new DecisionResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(Ct));
    }

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
            property.Name.ShouldNotContain("guardrail", Case.Insensitive);
        }

        var text = view.GetRawText();
        foreach (var term in RiskVocabulary)
        {
            text.ShouldNotContain(term, Case.Insensitive);
        }
    }

    /// <summary>Reviewer reasons as a claims agent must see them: risk-related ones collapsed into one label, in place of the first.</summary>
    private static List<string> AsSeenByAgents(IEnumerable<string> reasons)
    {
        var result = new List<string>();
        foreach (var reason in reasons)
        {
            var shown = RiskRelatedReasons.Contains(reason) ? EscalationReasonExtensions.AgentSafeLabel : reason;
            if (shown != EscalationReasonExtensions.AgentSafeLabel || !result.Contains(shown))
            {
                result.Add(shown);
            }
        }

        return result;
    }

    private static JsonObject Without(JsonObject decision, string member)
    {
        var copy = decision.DeepClone().AsObject();
        copy.Remove(member);
        return copy;
    }

    private static JsonObject With(JsonObject decision, string member, string value)
    {
        var copy = decision.DeepClone().AsObject();
        copy[member] = value;
        return copy;
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<ClaimRow> ClaimRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select reference, status, final_outcome, final_decided_by, final_explanation, finalized_at, auto_info_request_count, reviewer_info_requested
            from claims.claims where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} not found");
        return new ClaimRow(
            reader.GetString(0),
            WireName.Parse<ClaimStatus>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : WireName.Parse<FinalOutcome>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : WireName.Parse<DecidedBy>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetInt32(6),
            reader.GetBoolean(7));
    }

    private Task<string> SubmittedByAsync(Guid claimId) => ScalarAsync<string>("select submitted_by from claims.claims where id = @value", claimId);

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

    /// <summary>The run's recommendation row as JSON text, or null when it has none — to show it never changes (FR-036).</summary>
    private async Task<string?> RecommendationRowAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select row_to_json(r)::text from adjudication.recommendations r where run_id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private Task<string> RecommendationDecisionAsync(Guid runId)
        => ScalarAsync<string>("select decision from adjudication.recommendations where run_id = @value and is_valid", runId);

    private Task<long> ValidRecommendationCountAsync(Guid runId)
        => CountAsync("select count(*) from adjudication.recommendations where run_id = @value and is_valid", runId);

    /// <summary>The run's escalation reason codes.</summary>
    private async Task<List<string>> EscalationReasonsAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select reasons::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : r.GetProperty("code").GetString()!)
            .ToList();
    }

    private async Task<List<ReviewDecisionRow>> ReviewDecisionRowsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select decision, overrides_ai, reviewer_sub, justification, claimant_explanation
            from review.review_decisions where claim_id = @id order by decided_at
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<ReviewDecisionRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new ReviewDecisionRow(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>The claim's decision trail in order.</summary>
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

    private async Task<List<SecurityEventRow>> SecurityEventsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select id, tenant_id, kind, target from audit.security_events", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<SecurityEventRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new SecurityEventRow(
                reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>An escalated claim as observed before any decision.</summary>
    /// <param name="Queues">Each observed staff user's review queue item for the claim, or null when the queue does not list it.</param>
    /// <param name="RecommendationRow">The latest run's recommendation row as JSON text, or null when there is none.</param>
    private sealed record Escalation(
        Guid ClaimId,
        ClaimStatus Status,
        Guid RunId,
        IReadOnlyDictionary<string, JsonElement?> Queues,
        DetailResponse ReviewerDetail,
        DetailResponse AgentDetail,
        string? RecommendationRow)
    {
        public JsonElement QueueItem(string username)
            => Queues[username] ?? throw new ShouldAssertException($"The claim is not in {username}'s review queue.");
    }

    private sealed record DetailResponse(HttpStatusCode Status, string? ETag, JsonElement Body);

    private sealed record DecisionResponse(HttpStatusCode Status, string? MediaType, string Body)
    {
        public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Body);

        /// <summary>A 400 ValidationProblemDetails naming <paramref name="member"/> among its errors.</summary>
        public void ShouldBeValidationProblem(string member)
        {
            Status.ShouldBe(HttpStatusCode.BadRequest, Body);
            MediaType.ShouldBe("application/problem+json");
            Json.GetProperty("errors").EnumerateObject().Select(e => e.Name)
                .ShouldContain(name => name.Equals(member, StringComparison.OrdinalIgnoreCase), Body);
        }
    }

    private sealed record ClaimRow(
        string Reference,
        ClaimStatus Status,
        FinalOutcome? FinalOutcome,
        DecidedBy? FinalDecidedBy,
        string? FinalExplanation,
        DateTimeOffset? FinalizedAt,
        int AutoInfoRequestCount,
        bool ReviewerInfoRequested);

    private sealed record RunRow(Guid Id, Disposition? Disposition);

    private sealed record ReviewDecisionRow(string Decision, bool OverridesAi, string ReviewerSub, string? Justification, string? ClaimantExplanation);

    private sealed record TrailRow(string Step, string Actor, string Payload);

    private sealed record SecurityEventRow(Guid Id, Guid? TenantId, string Kind, string? Target);
}
