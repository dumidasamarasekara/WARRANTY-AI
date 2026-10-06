using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// User story 5 end to end (quickstart S6, S16, S17 and the US5 scenarios of seed/golden/scenarios.json):
/// a claim with missing or unusable information is asked for the specific items instead of being decided
/// (FR-010, FR-029), a supplement starts a full re-evaluation whose run is kept next to the earlier one,
/// missing information never hides an escalation condition (FR-010/FR-028 precedence, research R24), at
/// most two automatic requests are made, and a claim returned after a reviewer's request goes back to
/// review whatever the AI recommends (FR-034). Claims are adjudicated with recorded AI responses.
/// <para>
/// Every round of a claim runs in the fixture's one app instance: the replay provider numbers each
/// agent's model calls per claim in memory, so round 2 reads <c>intake-2.json</c> and so on.
/// </para>
/// <para>
/// <b>Claims without an invoice</b> (S6-round1, US5-missing-and-high-value) are created through the
/// application layer, not <c>POST /api/public/claims</c>: the submission endpoint (<see cref="SubmitClaim"/>,
/// and contracts/rest-api.openapi.yaml <c>ClaimSubmissionForm</c>, which lists <c>invoice</c> as required)
/// refuses a submission without an invoice with 400 "Add the invoice.", while spec.md US5 scenario 1 expects
/// such a claim to reach "Pending Information". Until that mismatch is decided, the test stores the claim, its
/// photos and its round-1 job exactly as <see cref="SubmitClaim"/> would after validation, so the
/// adjudication (intake short-circuit, guardrails, action) is exercised unchanged.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class US5_RequestInformationTests(WarrantyAppFixture fixture)
{
    /// <summary>The note a claimant sends with a scenario supplement.</summary>
    private const string SupplementNote = "Here are the documents you asked for.";

    /// <summary>Guards <see cref="_s6"/>: the S6 claim through both rounds, run once and shared by the S6 tests.</summary>
    private static readonly SemaphoreSlim S6Gate = new(1, 1);

    private static Task<S6Flow>? _s6;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- S6: missing invoice, supplement, full re-evaluation -------------------------------------

    [Fact]
    public async Task S6_round1_without_an_invoice_is_PendingInformation_asking_for_the_invoice_without_evidence_policy_or_decision_calls()
    {
        var scenario = GoldenScenario.Load("S6-round1");

        var flow = await S6FlowAsync();

        var round1 = flow.Round1;
        ShouldMatch(scenario.Outcome, round1);
        round1.ModelCallAgents.ShouldBe(["intake"], "only the intake model is called before the short-circuit");
        round1.ModelCallAgents.ShouldNotContain("evidence");
        round1.ModelCallAgents.ShouldNotContain("policy");
        round1.ModelCallAgents.ShouldNotContain("decision");
        scenario.RequestedItems.ShouldContain("INVOICE");
        round1.RecommendationDecision.ShouldBeNull("no coverage decision is made while information is missing");

        // The claimant is told specifically that the invoice is needed (FR-029) and sees no internal data.
        var view = flow.Round1ClaimantView;
        view.GetProperty("status").GetString().ShouldBe("PendingInformation");
        var requested = view.GetProperty("requestedItems").EnumerateArray().ToList();
        requested.Select(i => i.GetProperty("item").GetString()).ShouldContain("INVOICE");
        requested.Single(i => i.GetProperty("item").GetString() == "INVOICE").GetProperty("reason").GetString()
            .ShouldNotBeNull().ShouldContain("invoice", Case.Insensitive);
        view.TryGetProperty("outcomeExplanation", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task S6_round2_after_the_claimant_supplies_the_invoice_runs_every_step_and_the_trail_keeps_both_runs()
    {
        var scenario = GoldenScenario.Load("S6-round2");
        scenario.ClaimOf.ShouldBe("S6-round1");
        scenario.Round.ShouldBe(2);
        scenario.Supplement.ShouldNotBeNull().Invoice.ShouldNotBeNull();

        var flow = await S6FlowAsync();

        flow.SupplementStatus.ShouldBe(HttpStatusCode.Accepted);
        flow.SupplementBody.GetProperty("round").GetInt32().ShouldBe(2);
        flow.SupplementBody.TryGetProperty("claimId", out _).ShouldBeFalse("the claimant route never exposes internal IDs");

        var round2 = flow.Round2;
        ShouldMatch(scenario.Outcome, round2);
        round2.ModelCallAgents.ShouldBe(["decision", "evidence", "intake", "policy"], ignoreOrder: true);
        round2.RunId.ShouldNotBe(flow.Round1.RunId);

        // Both evaluations are kept: two completed runs, two guardrail evaluations, the supplement between them.
        var steps = round2.Trail.Select(e => e.Step).ToList();
        steps.Count(s => s == "GuardrailsEvaluated").ShouldBe(2);
        var supplementAt = steps.IndexOf("SupplementReceived");
        supplementAt.ShouldBeGreaterThan(steps.IndexOf("InformationRequested"));
        steps.LastIndexOf("GuardrailsEvaluated").ShouldBeGreaterThan(supplementAt);
        steps.ShouldContain("AutoApproved");
        (await CountAsync("select count(*) from adjudication.guardrail_evaluations g join adjudication.adjudication_runs r on r.id = g.run_id where r.claim_id = @value", flow.ClaimId))
            .ShouldBe(2);
        (await CountAsync("select count(*) from claims.claim_evidence where claim_id = @value and round = 2 and kind = 'Invoice'", flow.ClaimId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_supplement_to_an_Approved_claim_is_409_and_starts_no_round()
    {
        var flow = await S6FlowAsync();
        flow.Round2.Status.ShouldBe(ClaimStatus.Approved);
        var supplement = GoldenScenario.Load("S6-round2").Supplement!;

        using var client = fixture.CreateClaimantClient(GoldenScenario.Load("S6-round1").ChannelHost, flow.ClaimantToken);
        using var response = await client.PostAsync($"/api/public/claims/{flow.Reference}/supplements", supplement.ToForm("One more copy."), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await ScalarAsync<int>("select current_round from claims.claims where id = @value", flow.ClaimId)).ShouldBe(2);
        (await ClaimStatusAsync(flow.ClaimId)).ShouldBe(ClaimStatus.Approved);
        (await CountAsync("select count(*) from adjudication.adjudication_runs where claim_id = @value", flow.ClaimId)).ShouldBe(2);
    }

    // ---- Unclear photos: the AI asks for damage photos, no risk signal -----------------------------

    [Fact]
    public async Task Unclear_photos_are_PendingInformation_asking_for_damage_photos_with_no_risk_signal()
    {
        var scenario = GoldenScenario.Load("US5-unclear-photos");

        var claim = await SubmitAsClaimantAsync(scenario);
        var round1 = await ObserveRoundAsync(scenario, claim.ClaimId, 1);

        ShouldMatch(scenario.Outcome, round1);
        scenario.RequestedItems.ShouldBe(["PHOTO_OF_DAMAGE"]);
        scenario.Outcome.Has("riskSignals").ShouldBeTrue();
        round1.RiskSignals.ShouldNotBeNull().ShouldBeEmpty("unclear photos are missing information, not a risk signal (FR-019)");
        round1.EscalationReasons.ShouldBeEmpty();
        round1.ModelCallAgents.ShouldBe(["decision", "evidence", "intake", "policy"], ignoreOrder: true);

        var view = await ClaimantViewAsync(scenario, claim.Reference, await ClaimantTokenAsync(scenario, claim.Reference));
        view.GetProperty("requestedItems").EnumerateArray().Select(i => i.GetProperty("item").GetString()).ShouldBe(["PHOTO_OF_DAMAGE"]);
    }

    // ---- Missing invoice and a value above the limit: review wins ----------------------------------

    [Fact]
    public async Task A_missing_invoice_on_a_claim_above_the_value_limit_goes_to_review_instead_of_a_request()
    {
        var scenario = GoldenScenario.Load("US5-missing-and-high-value");
        scenario.HasInvoice.ShouldBeFalse();

        var claim = await SubmitWithoutInvoiceAsync(scenario);
        var round1 = await ObserveRoundAsync(scenario, claim.ClaimId, 1);

        ShouldMatch(scenario.Outcome, round1);
        round1.Status.ShouldBe(ClaimStatus.UnderReview);
        round1.AutoInfoRequestCount.ShouldBe(0, "no automatic request is made when the claim must be reviewed anyway");
        round1.ModelCallAgents.ShouldBe(["intake"]);
        round1.Trail.Select(e => e.Step).ShouldNotContain("InformationRequested");
        var item = round1.QueueItem(TestStaffUsers.ReviewerAurora.Username);
        item.GetProperty("claimValue").GetDecimal().ShouldBe((decimal)scenario.Expected["claimValue"]!);
    }

    // ---- S17: at most two automatic requests -----------------------------------------------------

    [Fact]
    public async Task S17_an_illegible_invoice_is_requested_twice_and_the_third_round_goes_to_review()
    {
        var scenario = GoldenScenario.Load("S17");
        var rounds = scenario.Rounds;
        rounds.Select(r => r.Round).ShouldBe([2, 3]);

        var claim = await SubmitAsClaimantAsync(scenario);
        var round1 = await ObserveRoundAsync(scenario, claim.ClaimId, 1);
        ShouldMatch(scenario.Outcome, round1);
        round1.AutoInfoRequestCount.ShouldBe(1);

        var token = await ClaimantTokenAsync(scenario, claim.Reference);
        var observed = round1;
        foreach (var round in rounds)
        {
            observed.Status.ShouldBe(ClaimStatus.PendingInformation, $"round {round.Round} needs a pending claim");
            await SupplementAsync(scenario, claim.Reference, token, round);
            observed = await ObserveRoundAsync(scenario, claim.ClaimId, round.Round);
            ShouldMatch(round.Expected, observed);
        }

        var round3 = observed;
        round3.Status.ShouldBe(ClaimStatus.UnderReview);
        round3.AutoInfoRequestCount.ShouldBe(2);
        round3.Checks["AUTO_INFO_REQUESTS_WITHIN_LIMIT"].ShouldBeFalse();
        round3.EscalationReasons.ShouldContain("INFO_INCOMPLETE_AFTER_2_REQUESTS");
        round3.QueueItem(TestStaffUsers.ReviewerAurora.Username).GetProperty("escalationReasons").EnumerateArray()
            .Select(r => r.GetString()).ShouldContain("information still incomplete after 2 requests");
        round3.Trail.Count(e => e.Step == "InformationRequested").ShouldBe(2);
    }

    // ---- S16: returned after a reviewer's request ------------------------------------------------

    [Fact]
    public async Task S16_a_claim_returned_after_a_reviewer_request_goes_back_to_review_although_the_AI_recommends_APPROVE()
    {
        var scenario = GoldenScenario.Load("S16");
        var round2 = scenario.Rounds.ShouldHaveSingleItem();
        round2.Round.ShouldBe(2);

        // Round 1: the intake model call fails, so the claim is escalated without an AI analysis.
        var claim = await SubmitAsClaimantAsync(scenario);
        var round1 = await ObserveRoundAsync(scenario, claim.ClaimId, 1);
        ShouldMatch(scenario.Outcome, round1);
        round1.Status.ShouldBe(ClaimStatus.UnderReview);

        // reviewer.aurora asks for a photo of the serial label.
        var reviewer = TestStaffUsers.Find(scenario.Reviewer!);
        var decision = scenario.ReviewerDecision;
        var recorded = await DecideAsync(reviewer, claim.ClaimId, decision);
        recorded.StatusCode.ShouldBe(HttpStatusCode.Created, recorded.Body);
        var pending = await ClaimRowAsync(claim.ClaimId);
        pending.Status.ShouldBe(ClaimStatus.PendingInformation);
        pending.ReviewerInfoRequested.ShouldBeTrue();
        pending.AutoInfoRequestCount.ShouldBe(0, "a reviewer's request is not an automatic one");

        var token = await ClaimantTokenAsync(scenario, claim.Reference);
        var view = await ClaimantViewAsync(scenario, claim.Reference, token);
        view.GetProperty("requestedItems").EnumerateArray().Select(i => i.GetProperty("item").GetString()).ShouldBe(["PHOTO_OF_SERIAL_LABEL"]);

        // The claimant supplies the photo; round 2 runs every agent and the AI recommends APPROVE ...
        round2.Supplement.Photos.ShouldNotBeEmpty();
        await SupplementAsync(scenario, claim.Reference, token, round2);
        var observed = await ObserveRoundAsync(scenario, claim.ClaimId, 2);

        observed.ModelCallAgents.ShouldBe(["decision", "evidence", "intake", "policy"], ignoreOrder: true);
        observed.RecommendationDecision.ShouldBe("APPROVE");

        // ... yet the claim returns to review, never finalized automatically (FR-034).
        ShouldMatch(round2.Expected, observed);
        observed.Status.ShouldBe(ClaimStatus.UnderReview);
        observed.Checks["NOT_RETURNED_FROM_REVIEW"].ShouldBeFalse();
        observed.EscalationReasons.ShouldContain("RETURNED_AFTER_REVIEWER_REQUEST");
        observed.QueueItem(reviewer.Username).GetProperty("escalationReasons").EnumerateArray()
            .Select(r => r.GetString()).ShouldContain("returned after reviewer information request");
        var final = await ClaimRowAsync(claim.ClaimId);
        final.FinalOutcome.ShouldBeNull();
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claim.ClaimId)).ShouldBe(0);
    }

    // ---- Shared S6 flow ---------------------------------------------------------------------------

    /// <summary>The S6 claim: round 1 without an invoice, observed; the S6-round2 supplement; round 2, observed. Runs once.</summary>
    private async Task<S6Flow> S6FlowAsync()
    {
        await S6Gate.WaitAsync(Ct);
        try
        {
            _s6 ??= RunS6Async();
        }
        finally
        {
            S6Gate.Release();
        }

        return await _s6;
    }

    private async Task<S6Flow> RunS6Async()
    {
        var round1Scenario = GoldenScenario.Load("S6-round1");
        var round2Scenario = GoldenScenario.Load("S6-round2");

        var claim = await SubmitWithoutInvoiceAsync(round1Scenario);
        var round1 = await ObserveRoundAsync(round1Scenario, claim.ClaimId, 1);
        var token = await ClaimantTokenAsync(round1Scenario, claim.Reference);
        var view = await ClaimantViewAsync(round1Scenario, claim.Reference, token);

        using var client = fixture.CreateClaimantClient(round1Scenario.ChannelHost, token);
        using var response = await client.PostAsync(
            $"/api/public/claims/{claim.Reference}/supplements", round2Scenario.Supplement!.ToForm(SupplementNote), Ct);
        var body = response.StatusCode == HttpStatusCode.Accepted ? await response.Content.ReadFromJsonAsync<JsonElement>(Ct) : default;
        var round2 = response.StatusCode == HttpStatusCode.Accepted ? await ObserveRoundAsync(round2Scenario, claim.ClaimId, 2) : null;

        return new S6Flow(claim.ClaimId, claim.Reference, token, round1, view, response.StatusCode, body, round2!);
    }

    // ---- Expectations ---------------------------------------------------------------------------------

    /// <summary>Asserts every key the scenario's <c>expected</c> object (or a round's) sets against the observed round.</summary>
    private static void ShouldMatch(ScenarioExpectation expected, RoundObservation observed)
    {
        var at = $"round {observed.Round}";
        observed.Status.ToString().ShouldBe(expected.Status, at);
        if (expected.Disposition is { } disposition)
        {
            observed.Disposition.ShouldBe(disposition, at);
        }

        if (expected.DecidedBy is { } decidedBy)
        {
            observed.FinalDecidedBy.ShouldBe(decidedBy, at);
        }

        if (expected.Has("recommendation"))
        {
            observed.RecommendationDecision.ShouldBe(expected.Recommendation, at);
        }

        if (expected.Coverage is { } coverage)
        {
            observed.Coverage.ShouldBe(coverage, at);
        }

        if (expected.RiskLevel is { } riskLevel)
        {
            observed.RiskLevel.ShouldBe(riskLevel, at);
        }

        if (expected.Has("riskSignals"))
        {
            observed.RiskSignals.ShouldNotBeNull(at).ShouldBe(expected.RiskSignals, ignoreOrder: true, at);
        }

        if (expected.Has("requestedItems"))
        {
            observed.RequestedItems.ShouldBe(expected.RequestedItems, ignoreOrder: true, at);
        }
        else if (observed.Status != ClaimStatus.PendingInformation)
        {
            observed.RequestedItems.ShouldBeEmpty(at);
        }

        if (expected.AutoInfoRequestCount is { } count)
        {
            observed.AutoInfoRequestCount.ShouldBe(count, at);
        }

        if (expected.Has("modelCalls"))
        {
            observed.ModelCallAgents.ShouldBe(expected.ModelCalls, ignoreOrder: true, at);
        }

        if (expected.Has("aiStepFailed"))
        {
            observed.FailedAgents.ShouldBe(expected.AiStepFailed, ignoreOrder: true, at);
        }

        foreach (var code in expected.FailedGuardrails)
        {
            observed.Checks.ShouldContainKey(code, at);
            observed.Checks[code].ShouldBeFalse($"{at}: {code} must fail");
        }

        foreach (var code in expected.PassedGuardrails)
        {
            observed.Checks.ShouldContainKey(code, at);
            observed.Checks[code].ShouldBeTrue($"{at}: {code} must pass");
        }

        if (expected.Has("escalationReasons"))
        {
            observed.EscalationReasons.ShouldBe(expected.EscalationReasons, ignoreOrder: true, at);
        }

        if (expected.EscalationReasonText is { } label)
        {
            foreach (var username in expected.ReviewQueues)
            {
                observed.QueueItem(username).GetProperty("escalationReasons").EnumerateArray().Select(r => r.GetString()).ShouldContain(label, at);
            }
        }

        foreach (var username in expected.ReviewQueues)
        {
            observed.Queues[username].ShouldNotBeNull($"{at}: the claim must be in {username}'s review queue");
        }

        if (expected.Has("citedClauseKeys"))
        {
            observed.CitedClauseKeys.ShouldBe(expected.CitedClauseKeys, ignoreOrder: true, at);
        }

        if (expected.Has("runRounds"))
        {
            observed.RunRounds.ShouldBe(expected.RunRounds, at);
        }

        if (expected.LastTrailEntries.Count > 0)
        {
            // Only the executed side effects (notifications, repair requests) may follow the outcome's entries.
            var steps = observed.Trail.Select(e => e.Step).Where(s => s != "ActionExecuted").ToList();
            steps.TakeLast(expected.LastTrailEntries.Count).ShouldBe(expected.LastTrailEntries, at);
        }
    }

    // ---- Submission and supplements ---------------------------------------------------------------

    /// <summary>Submits the scenario through its tenant's claimant channel.</summary>
    private async Task<SubmittedClaim> SubmitAsClaimantAsync(GoldenScenario scenario)
    {
        scenario.HasInvoice.ShouldBeTrue($"{scenario.ScenarioId} has no invoice; use SubmitWithoutInvoiceAsync");
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsync("/api/public/claims", scenario.ToSubmission(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
        return new SubmittedClaim(await ScalarAsync<Guid>("select id from claims.claims where reference = @value", reference), reference);
    }

    /// <summary>
    /// Stores a claimant-channel claim without an invoice and its round-1 job the way <see cref="SubmitClaim"/>
    /// does after validation (the claim data is validated with <see cref="SubmitClaim.Validate"/>; the photos go
    /// through the upload sanitizer and the document store). The public API refuses such a submission
    /// ("Add the invoice.") although spec.md US5 expects it to reach PendingInformation — see the class summary.
    /// </summary>
    private async Task<SubmittedClaim> SubmitWithoutInvoiceAsync(GoldenScenario scenario)
    {
        scenario.HasInvoice.ShouldBeFalse();
        var tenantId = TenantIdOf(scenario.Tenant);
        var correlationId = $"it-us5-{Guid.NewGuid():N}"[..32];
        using var tenant = TenantContextScope.Begin(tenantId, scenario.Tenant, Claim.ClaimantSubmitter, correlationId);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var data = scenario.ClaimJson().Deserialize<ClaimSubmissionData>(JsonSerializerOptions.Web).ShouldNotBeNull();
        SubmitClaim.Validate(data, DateOnly.FromDateTime(now.UtcDateTime)).ShouldBeEmpty();
        var customer = data.Customer!;
        var purchase = data.Purchase!;
        var product = await services.GetRequiredService<ICatalogRepository>().FindProductByModelAsync(data.Product!.ModelCode!, Ct);
        product.ShouldNotBeNull($"{data.Product.ModelCode} must be in the {scenario.Tenant} catalog");
        var crmCustomer = await services.GetRequiredService<ICrmClient>().FindOrCreateCustomerAsync(
            new CustomerDetails(customer.FullName!, customer.Email!, customer.Country!, customer.Phone, customer.AddressLine, customer.City, customer.PostalCode),
            Ct);

        var claimId = Guid.CreateVersion7();
        var claim = Claim.Submit(
            claimId, tenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, crmCustomer.Id,
            customer.Email, customer.Phone, data.Product.ModelCode!, product.Id, data.Product.SerialNumber!,
            DateOnly.ParseExact(purchase.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture), purchase.Place!, purchase.Price!.Value,
            SubmitClaim.DeriveRegion(purchase.Country, customer.Country), data.ProblemDescription!, now);

        var sanitizer = services.GetRequiredService<IUploadSanitizer>();
        var documents = services.GetRequiredService<IDocumentStore>();
        var evidence = new List<ClaimEvidence>();
        foreach (var photo in scenario.PhotoFiles)
        {
            await using var original = new MemoryStream(GoldenScenario.EvidenceBytes(photo), writable: false);
            var clean = (await sanitizer.SanitizeAsync(original, Ct)).ShouldBeOfType<UploadSanitizerResult.Sanitized>();
            var evidenceId = Guid.CreateVersion7();
            await using var content = new MemoryStream(clean.Content.ToArray(), writable: false);
            var stored = await documents.UploadEvidenceAsync(
                ClaimEvidence.BlobPathFor(claimId, claim.CurrentRound, evidenceId, clean.ContentType), content, clean.ContentType, Ct);
            evidence.Add(ClaimEvidence.Create(
                evidenceId, tenantId, claimId, claim.CurrentRound, EvidenceKind.Photo, Path.GetFileName(photo), clean.ContentType,
                stored.SizeBytes, stored.Sha256, now));
        }

        var job = ClaimJob.Enqueue(Guid.CreateVersion7(), tenantId, claimId, claim.CurrentRound, correlationId, now);
        var claims = services.GetRequiredService<IClaimRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var trail = services.GetRequiredService<IDecisionTrailWriter>();
        await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                claims.Add(claim);
                foreach (var item in evidence)
                {
                    claims.AddEvidence(item);
                }

                await services.GetRequiredService<IJobQueue>().EnqueueAsync(job, token);
                await unitOfWork.SaveChangesAsync(token);
                await trail.AppendAsync(
                    claimId, TrailStep.ClaimSubmitted, Claim.ClaimantSubmitter,
                    $"Claim {claim.Reference} submitted through the claimant channel.",
                    new { reference = claim.Reference, channel = claim.Channel.ToString(), round = claim.CurrentRound }, token);
                await trail.AppendAsync(
                    claimId, TrailStep.EvidenceStored, "system", $"0 invoices and {evidence.Count} photos stored for round 1.",
                    new { round = claim.CurrentRound, evidence = evidence.Select(e => new { evidenceId = e.Id, kind = e.Kind.ToString() }).ToList() }, token);
            },
            Ct);

        return new SubmittedClaim(claimId, claim.Reference);
    }

    /// <summary>Sends the round's supplement through <c>POST /api/public/claims/{reference}/supplements</c> as the claimant.</summary>
    private async Task SupplementAsync(GoldenScenario scenario, string reference, string token, ScenarioRound round)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost, token);
        using var response = await client.PostAsync($"/api/public/claims/{reference}/supplements", round.Supplement.ToForm(SupplementNote), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("round").GetInt32().ShouldBe(round.Round);
    }

    private async Task<string> ClaimantTokenAsync(GoldenScenario scenario, string reference)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = scenario.ContactEmail }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }

    private async Task<JsonElement> ClaimantViewAsync(GoldenScenario scenario, string reference, string token)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost, token);
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

    // ---- Observation ------------------------------------------------------------------------------

    /// <summary>Waits until the claim's run of <paramref name="round"/> completed and the claim settled, then reads what the round produced.</summary>
    private async Task<RoundObservation> ObserveRoundAsync(GoldenScenario scenario, Guid claimId, int round)
    {
        await WaitForRunAsync(claimId, round);
        var status = await fixture.WaitForClaimStatusAsync(
            claimId, s => s is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation), TimeSpan.FromSeconds(90));
        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe(status);
        var runId = await ScalarAsync<Guid>(
            $"select id from adjudication.adjudication_runs where claim_id = @value and round = {round}", claimId);

        using var reviewerClient = fixture.CreateStaffClient($"reviewer.{scenario.Tenant}");
        using var detailResponse = await reviewerClient.GetAsync($"/api/claims/{claimId}", Ct);
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var requestedItems = detail.GetProperty("requestedItems").EnumerateArray().Select(i => i.GetProperty("item").GetString()!).ToList();

        var queues = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var username in new[] { $"reviewer.{scenario.Tenant}" }.Concat(scenario.ReviewQueues).Concat(scenario.Rounds.SelectMany(r => r.Expected.ReviewQueues)).Distinct())
        {
            using var client = fixture.CreateStaffClient(username);
            using var response = await client.GetAsync("/api/review-queue", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, username);
            queues[username] = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray()
                .Where(i => i.GetProperty("claimId").GetGuid() == claimId).Cast<JsonElement?>().SingleOrDefault();
        }

        var recommendation = await RecommendationAsync(runId);
        var risk = await RiskAsync(runId);
        var trail = await TrailAsync(claimId);
        return new RoundObservation(
            round,
            runId,
            claim.Status,
            await NullableScalarAsync<string>("select disposition from adjudication.adjudication_runs where id = @value", runId),
            claim.FinalDecidedBy,
            claim.AutoInfoRequestCount,
            requestedItems,
            recommendation?.Decision,
            recommendation?.Coverage,
            risk?.Level,
            risk?.Signals,
            await ModelCallAgentsAsync(runId),
            trail.Where(e => e.Step == "AiStepFailed" && e.Payload.Contains(runId.ToString(), StringComparison.OrdinalIgnoreCase)).Select(e => e.Actor).ToList(),
            await GuardrailChecksAsync(runId),
            await EscalationReasonsAsync(runId),
            await CitedClauseKeysAsync(runId),
            await ListAsync<int>("select round from adjudication.adjudication_runs where claim_id = @value order by round", claimId),
            queues,
            trail);
    }

    private async Task WaitForRunAsync(Guid claimId, int round)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            while (await CountAsync(
                       $"select count(*) from adjudication.adjudication_runs where claim_id = @value and round = {round} and status = 'Completed'", claimId) == 0)
            {
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Round {round} of claim {claimId} did not complete; claim status {await ClaimStatusAsync(claimId)}, " +
                $"jobs: {string.Join(", ", await ListAsync<string>("select round || ':' || status || ':' || attempts from claims.claim_jobs where claim_id = @value", claimId))}.");
        }
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<ClaimRow> ClaimRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select status, final_outcome, final_decided_by, auto_info_request_count, reviewer_info_requested from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} not found");
        return new ClaimRow(
            WireName.Parse<ClaimStatus>(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.GetBoolean(4));
    }

    private async Task<ClaimStatus> ClaimStatusAsync(Guid claimId) => (await ClaimRowAsync(claimId)).Status;

    private async Task<(string Decision, string? Coverage)?> RecommendationAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select decision, coverage from adjudication.recommendations where run_id = @id and is_valid", connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    /// <summary>The run's risk level and signal codes, or null when it has no risk assessment.</summary>
    private async Task<(string Level, List<string> Signals)?> RiskAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select level, signals::text from adjudication.risk_assessments where run_id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        {
            return null;
        }

        using var signals = JsonDocument.Parse(reader.GetString(1));
        return (reader.GetString(0), signals.RootElement.EnumerateArray().Select(s => s.GetProperty("code").GetString()!).Distinct().ToList());
    }

    /// <summary>The agents whose model the run called (an agent's sub-routes, e.g. <c>evidence-photo</c>, count as the agent).</summary>
    private async Task<List<string>> ModelCallAgentsAsync(Guid runId)
        => (await ListAsync<string>("select agent from aiops.model_calls where run_id = @value", runId))
            .Select(a => a.Split('-', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The clause keys the run's valid recommendation cites as <c>SUPPORTS_COVERAGE</c>, <c>SUPPORTS_REJECTION</c> or
    /// <c>DEFINES_PERIOD</c> (how scenarios.json labels <c>citedClauseKeys</c>; <c>CONTEXT</c> references do not count).
    /// </summary>
    private async Task<List<string>> CitedClauseKeysAsync(Guid runId)
    {
        var json = await NullableScalarAsync<string>(
            "select policy_refs::text from adjudication.recommendations where run_id = @value and is_valid", runId);
        if (json is null)
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var refIds = document.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("relevance").GetString() is "SUPPORTS_COVERAGE" or "SUPPORTS_REJECTION" or "DEFINES_PERIOD")
            .Select(r => (r.TryGetProperty("ref", out var id) ? id : r.GetProperty("refId")).GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select ref_id, clause_key from adjudication.retrieved_policy_refs where run_id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var keys = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            if (refIds.Contains(reader.GetString(0)))
            {
                keys.Add(reader.GetString(1));
            }
        }

        return keys;
    }

    /// <summary>Guardrail check code → passed, from the run's ordered <c>checks</c>.</summary>
    private async Task<Dictionary<string, bool>> GuardrailChecksAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select checks::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("passed").GetBoolean());
    }

    private async Task<List<string>> EscalationReasonsAsync(Guid runId)
    {
        var json = await ScalarAsync<string>("select reasons::text from adjudication.guardrail_evaluations where run_id = @value", runId);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : r.GetProperty("code").GetString()!)
            .ToList();
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

    private Task<long> CountAsync(string sql, object value) => ScalarAsync<long>(sql, value);

    private async Task<T> ScalarAsync<T>(string sql, object value)
        => await NullableScalarAsync<T>(sql, value) is { } result ? result : throw new ShouldAssertException($"'{sql}' returned no {typeof(T).Name}.");

    private async Task<T?> NullableScalarAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return await command.ExecuteScalarAsync(Ct) is T result ? result : default;
    }

    private async Task<List<T>> ListAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static Guid TenantIdOf(string tenant) => tenant switch
    {
        "aurora" => TestStaffUsers.AuroraTenantId,
        "borealis" => TestStaffUsers.BorealisTenantId,
        _ => throw new ArgumentOutOfRangeException(nameof(tenant), tenant, null),
    };

    private sealed record SubmittedClaim(Guid ClaimId, string Reference);

    private sealed record ClaimRow(ClaimStatus Status, string? FinalOutcome, string? FinalDecidedBy, int AutoInfoRequestCount, bool ReviewerInfoRequested);

    private sealed record TrailRow(string Step, string Actor, string Payload);

    /// <summary>What one round of a claim produced, read once the round's run completed.</summary>
    /// <param name="RiskSignals">The run's risk signal codes, or null when it has no risk assessment.</param>
    /// <param name="FailedAgents">The agents of the run's <c>AiStepFailed</c> entries.</param>
    /// <param name="Queues">Each observed staff user's review queue item for the claim, or null when the queue does not list it.</param>
    private sealed record RoundObservation(
        int Round,
        Guid RunId,
        ClaimStatus Status,
        string? Disposition,
        string? FinalDecidedBy,
        int AutoInfoRequestCount,
        IReadOnlyList<string> RequestedItems,
        string? RecommendationDecision,
        string? Coverage,
        string? RiskLevel,
        IReadOnlyList<string>? RiskSignals,
        IReadOnlyList<string> ModelCallAgents,
        IReadOnlyList<string> FailedAgents,
        IReadOnlyDictionary<string, bool> Checks,
        IReadOnlyList<string> EscalationReasons,
        IReadOnlyList<string> CitedClauseKeys,
        IReadOnlyList<int> RunRounds,
        IReadOnlyDictionary<string, JsonElement?> Queues,
        IReadOnlyList<TrailRow> Trail)
    {
        public JsonElement QueueItem(string username)
            => Queues.TryGetValue(username, out var item) && item is { } found
                ? found
                : throw new ShouldAssertException($"Round {Round}: the claim is not in {username}'s review queue.");
    }

    private sealed record S6Flow(
        Guid ClaimId,
        string Reference,
        string ClaimantToken,
        RoundObservation Round1,
        JsonElement Round1ClaimantView,
        HttpStatusCode SupplementStatus,
        JsonElement SupplementBody,
        RoundObservation Round2);
}
