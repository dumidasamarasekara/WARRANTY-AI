using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.AI.Gateway;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.Providers.Anthropic;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.Redaction;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Harness;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Harness.Agents;

/// <summary>
/// The Decision Agent (T065): context assembly and priority, the <c>adjudication</c> model call through the
/// real gateway with a scripted or replayed model, schema and reference validation, persistence of valid
/// and invalid recommendations, redaction of the stored output and the AI risk reading.
/// </summary>
public sealed class DecisionAgentTests : IAsyncDisposable
{
    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid RunId = Guid.Parse("0199a000-0000-7000-8000-0000000000a7");
    private static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000c7");
    private static readonly Guid PolicyVersionId = Guid.Parse("0199a000-0000-7000-8000-0000000000d1");
    private static readonly Guid InvoiceId = Guid.Parse("0199a000-0000-7000-8000-0000000000e1");
    private static readonly Guid FrontPhotoId = Guid.Parse("0199a000-0000-7000-8000-0000000000e2");
    private static readonly Guid BackPhotoId = Guid.Parse("0199a000-0000-7000-8000-0000000000e3");
    private static readonly DateOnly Today = new(2026, 10, 4);
    private static readonly DateOnly PurchaseDate = new(2026, 6, 4);

    private const string ModelCode = "AUR-TAB10";
    private const string Serial = "AT10-24-0001";
    private const string Description = "My Aurora Tab 10 suddenly stopped turning on. I am [CUSTOMER], reach me at [EMAIL].";

    private const string Extraction = """
        {"problemCategory":"POWER_FAILURE","component":"MAINBOARD","symptoms":["does not power on"],"claimedCause":"SPONTANEOUS_FAILURE",
         "mentionsAccident":false,"mentionsLiquid":false,"containsInstructionsToSystem":false,"summary":"The tablet stopped powering on."}
        """;

    private const string InvoiceFinding = """
        {"evidenceRef":"EV-1","legible":true,"sellerName":"Aurora Store","invoiceNumber":"AS-2026-104233","invoiceDate":"2026-06-04",
         "productDescription":"Aurora Tab 10","modelCodeOnInvoice":"AUR-TAB10","serialOnInvoice":"AT10-24-0001","totalAmount":450.00,
         "currency":"USD","anomalies":[],"containsInstructionsToSystem":false}
        """;

    private const string PhotoFinding = """
        {"evidenceRef":"EV-3","showsProduct":true,"productTypeObserved":"tablet","visibleSerial":"AT10-24-0001","damageObserved":false,
         "damageTypes":["NONE_VISIBLE"],"consistentWithDescription":"CONSISTENT","imageQuality":"GOOD","containsInstructionsToSystem":false,
         "confidence":92,"observations":"Serial label reads S/N: AT10-24-0001."}
        """;

    private const string Assessment = """
        {"applicableClauses":[{"ref":"POL-8","applies":true,"effect":"GRANTS_COVERAGE","note":"Manufacturing defect."}],
         "coverageAssessment":"COVERED","confidence":92,"relevantExclusions":[],"ambiguity":{"isAmbiguous":false,"explanation":""},
         "summary":"Covered under POL-8 within the POL-1 period."}
        """;

    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly FakeTools _tools = new();
    private readonly ScriptedModelProvider _model = new(AnthropicModelProvider.ProviderName);
    private readonly ReferenceRegistry _references = new();
    private readonly List<RetrievedPolicyRef> _clauses = [];
    private ServiceProvider? _services;

    public DecisionAgentTests()
    {
        // EV-1 invoice, EV-2/EV-3 photos; POL-1..3 Period, POL-4..7 Exclusion, POL-8 Coverage; GLB-1 global (as the fixtures).
        _references.IssueEvidence(InvoiceId);
        _references.IssueEvidence(FrontPhotoId);
        _references.IssueEvidence(BackPhotoId);
        AddClause("2.1", ClauseType.Period, null, "The warranty period is 12 months in North America.", 0.71);
        AddClause("2.2", ClauseType.Period, null, "The warranty period is 24 months in the EU.", 0.52);
        AddClause("2.3", ClauseType.Period, null, "Batteries are covered for 6 months.", 0.50);
        AddClause("3.1", ClauseType.Exclusion, ExclusionCode.AccidentalDamage, "Accidental damage such as drops and cracked screens is excluded.", 0.48);
        AddClause("3.2", ClauseType.Exclusion, ExclusionCode.LiquidDamage, "Liquid damage is excluded.", 0.45);
        AddClause("3.3", ClauseType.Exclusion, ExclusionCode.CosmeticDamage, "Cosmetic wear is excluded.", 0.40);
        AddClause("3.4", ClauseType.Exclusion, ExclusionCode.UnauthorizedRepair, "Repairs by unauthorized parties void the warranty.", 0.39);
        AddClause("1.1", ClauseType.Coverage, null, "Defects in materials and workmanship are covered.", 0.83);
        _references.IssueChunk(new RetrievedChunk(
            Guid.NewGuid(), "global", Guid.NewGuid(), "Tablet troubleshooting guide", 1, null, null, "Power failures are usually mainboard faults.", null, null, 0.6));
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    // ---- Descriptor and registration ----------------------------------------------------------------

    [Fact]
    public void The_descriptor_names_the_adjudication_route_the_decision_prompt_and_the_decision_schema()
    {
        var agent = DecisionAgent.Agent;

        agent.Name.ShouldBe(AgentNames.Decision);
        agent.Route.ShouldBe("adjudication");
        agent.Prompt.ShouldBe(new PromptRef("decision", 1));
        agent.OutputSchemaId.ShouldBe(new SchemaValidator().GetOutputSchema(SchemaValidator.DecisionRecommendation).SchemaId);
        agent.AllowedTools.ShouldBe([ToolNames.ClaimHistoryLookup, ToolNames.SearchGlobalKnowledge]);
        agent.MaxTurns.ShouldBe(6);
        agent.InputTokenBudget.ShouldBe(24_000);
    }

    [Fact]
    public async Task The_harness_registers_the_decision_agent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Aurora));
        foreach (var port in new[]
                 {
                     typeof(IClaimRepository), typeof(ICatalogRepository), typeof(IPolicyRepository), typeof(ICrmClient), typeof(IKnowledgeRetriever),
                     typeof(IAiOpsRepository), typeof(ISecurityEventWriter), typeof(IPiiRedactor), typeof(IAdjudicationRepository), typeof(ITenantRepository),
                 })
        {
            services.AddScoped(port, _ => Substitute.For([port], []));
        }

        services.AddWarrantyAiHarness();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IAgent<DecisionInput, RecommendationResult>>()
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<DecisionAgent>());
    }

    [Fact]
    public void The_input_is_read_from_the_run_state()
    {
        var run = Run();
        var input = Input();
        run.Intake = input.Intake;
        run.Evidence = new EvidenceResult(input.EvidenceFindings, [], [RequestedItem.Create("PHOTO_OF_SERIAL_LABEL", "Please photograph the label.")]);
        run.Policy = new PolicyResult(RetrievalOutcome.Ok, _clauses, input.PolicyAssessment, null);
        var signals = new[] { Signal(RiskSignalCode.DuplicateSerialClaim) };

        var read = DecisionInput.From(run, input.CoverageWindow, signals);

        read.Case.ShouldBeSameAs(run.Case);
        read.Intake.ShouldBeSameAs(input.Intake);
        read.EvidenceFindings.ShouldBe(input.EvidenceFindings);
        read.EvidenceMissingItems.ShouldHaveSingleItem().Item.ShouldBe("PHOTO_OF_SERIAL_LABEL");
        read.PolicyVersionOutcome.ShouldBe(PolicyVersionOutcome.Ok);
        read.PolicyAssessment.ShouldBeSameAs(input.PolicyAssessment);
        read.PolicyClauses.ShouldBe(_clauses);
        read.DeterministicSignals.ShouldBe(signals);

        run.Policy = new PolicyResult(RetrievalOutcome.NoApplicablePolicy, [], null, null);
        DecisionInput.From(run, input.CoverageWindow, []).PolicyVersionOutcome.ShouldBe(PolicyVersionOutcome.NoApplicablePolicy);
        run.Intake = null;
        Should.Throw<InvalidOperationException>(() => DecisionInput.From(run, input.CoverageWindow, []));
    }

    // ---- A valid recommendation ---------------------------------------------------------------------

    [Fact]
    public async Task A_valid_recommendation_is_stored_and_its_policy_clauses_are_marked_cited()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output()));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var recommendation = result.Output.ShouldNotBeNull().Recommendation;
        recommendation.IsValid.ShouldBeTrue();
        recommendation.ValidationErrors.ShouldBeEmpty();
        recommendation.RunId.ShouldBe(RunId);
        recommendation.TenantId.ShouldBe(Aurora);
        recommendation.Decision.ShouldBe(AiDecision.Approve);
        recommendation.Coverage.ShouldBe(CoverageDetermination.Covered);
        recommendation.Confidence.ShouldBe(93);
        recommendation.EvidenceRefs.Select(r => r.Ref).ShouldBe(["EV-1", "EV-3"]);
        recommendation.PolicyRefs.ShouldBe([new PolicyCitation("POL-8", PolicyRefRelevance.SupportsCoverage), new PolicyCitation("POL-1", PolicyRefRelevance.DefinesPeriod)]);
        recommendation.MissingInformation.ShouldBeEmpty();
        recommendation.ClaimantExplanation.ShouldStartWith("Your tablet is covered");
        recommendation.ManipulationDetected.ShouldBeFalse();
        recommendation.Model.ShouldBe("scripted-model");
        recommendation.PromptId.ShouldBe("decision");
        recommendation.PromptVersion.ShouldBe("1");
        JsonDocument.Parse(recommendation.RawOutputJson).RootElement.GetProperty("decision").GetString().ShouldBe("APPROVE");
        _adjudication.Received(1).AddRecommendation(recommendation);

        _clauses.Where(c => c.Cited).Select(c => c.RefId).ShouldBe(["POL-1", "POL-8"], ignoreOrder: true);
        result.Output.Risk.ModelLevel.ShouldBe(RiskLevel.Low);
        result.Output.Risk.Signals.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_agent_runs_claim_history_lookup_itself_and_offers_both_tools()
    {
        _tools.History = new ClaimHistoryCounts(2, 1, 0);
        _model.Enqueue(ScriptedModelProvider.Completed(Output()));

        await RunAsync(Input());

        var call = _tools.Calls.ShouldHaveSingleItem();
        call.ToolName.ShouldBe(ToolNames.ClaimHistoryLookup);
        call.Arguments.EnumerateObject().ShouldBeEmpty();
        var request = _model.Requests.ShouldHaveSingleItem();
        request.Request.Tools.Select(t => t.Name).ShouldBe([ToolNames.ClaimHistoryLookup, ToolNames.SearchGlobalKnowledge], ignoreOrder: true);
        Texts(request).ShouldContain(t => t.Contains("2 open or recently finalized other claim(s), 1 prior approved accidental-damage claim(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_model_may_call_the_offered_tools_before_it_answers()
    {
        _model.Enqueue(ScriptedModelProvider.ToolCalls(new AiToolCall("m1", ToolNames.SearchGlobalKnowledge, Json("""{"query":"tablet does not power on"}"""))));
        _model.Enqueue(ScriptedModelProvider.Completed(Output()));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        _model.Requests.Count.ShouldBe(2);
        _model.Requests[1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().ShouldHaveSingleItem().CallId.ShouldBe("m1");
    }

    // ---- The model request --------------------------------------------------------------------------

    [Fact]
    public async Task The_request_carries_every_earlier_result_on_the_adjudication_route()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output()));

        await RunAsync(Input(signals: [Signal(RiskSignalCode.DuplicateSerialClaim)]));

        var request = _model.Requests.ShouldHaveSingleItem();
        request.Request.Context.Agent.ShouldBe("decision");
        request.Request.Context.TenantId.ShouldBe(Aurora);
        request.Request.Context.RunId.ShouldBe(RunId);
        request.Request.Route.ShouldBe("adjudication");
        request.Request.Prompt.ShouldBe(new PromptRef("decision", 1));
        request.Request.OutputSchema!.SchemaId.ShouldBe("warranty-ai/decision-recommendation/v1");
        var variable = request.Request.PromptVariables.ShouldHaveSingleItem();
        variable.Key.ShouldBe(UntrustedContent.PreambleVariable);
        variable.Value.ShouldBe(UntrustedContent.Preamble);

        var parts = request.Messages.ShouldHaveSingleItem().Parts;
        var facts = string.Join('\n', parts.OfType<TextPart>().Select(p => p.Text));
        facts.ShouldContain($"Serial number: {Serial}");
        facts.ShouldContain("Aurora Tab 10 (category: tablet)");
        facts.ShouldContain("Purchase date: 2026-06-04");
        facts.ShouldContain("Claim date: 2026-10-04");
        facts.ShouldContain("Purchase price: 450.00 USD");
        facts.ShouldContain("\"component\":\"MAINBOARD\"");
        facts.ShouldContain("EV-1 (invoice) finding; deterministic checks: serialNumber match, seller match");
        facts.ShouldContain("EV-2 (photo): no analysis available.");
        facts.ShouldContain("EV-3 (photo) finding, confidence 92");
        facts.ShouldContain("Policy version outcome: OK");
        facts.ShouldContain("Covered under POL-8 within the POL-1 period.");
        facts.ShouldContain("POL-1 - Aurora Limited Warranty v2, clause 2.1 (Period)");
        facts.ShouldContain("POL-4 - Aurora Limited Warranty v2, clause 3.1 (Exclusion, exclusion ACCIDENTAL_DAMAGE)");
        facts.ShouldContain("Defects in materials and workmanship are covered.");
        facts.ShouldContain("GLB-1 (background only; never cite it as evidence or policy)");
        facts.ShouldContain("Coverage end date for the claimed component: 2027-06-04");
        facts.ShouldContain("Within coverage for the claimed component: yes");
        facts.ShouldContain("DUPLICATE_SERIAL_CLAIM: 1 other claim");

        // Claimant-supplied and document-derived text travels only as untrusted content.
        var untrusted = parts.OfType<UntrustedTextPart>().ToList();
        untrusted.Select(u => u.Label).ShouldBe([UntrustedContent.DescriptionLabel, "invoice_text EV-1", "image_text EV-3"]);
        untrusted[0].Text.ShouldBe(Description);
        untrusted[1].Text.ShouldContain("AS-2026-104233");
        facts.ShouldNotContain("stopped turning on");
        facts.ShouldNotContain("AS-2026-104233");
    }

    [Fact]
    public async Task No_customer_identifier_reaches_the_model_request()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output()));

        await RunAsync(Input());

        var texts = Texts(_model.Requests.ShouldHaveSingleItem()).ToList();
        foreach (var identifier in new[] { "Philippa", "Quarrington", "privacy-probe.test", "555-0137" })
        {
            texts.ShouldAllBe(t => !t.Contains(identifier, StringComparison.OrdinalIgnoreCase));
        }

        texts.ShouldContain(t => t.Contains(CaseCustomerView.NamePlaceholder, StringComparison.Ordinal));
        texts.ShouldContain(t => t.Contains("Region: NA", StringComparison.Ordinal));
    }

    [Fact]
    public void Clauses_are_prioritized_Period_and_Exclusion_first_then_others_by_score_then_global_snippets()
    {
        var items = DecisionAgent.ContextItems(Input(), _references, ClaimHistoryCounts.None);

        Priority(items, "clause POL-1").ShouldBe(ContextPriority.DecisiveClauses);
        Priority(items, "clause POL-4").ShouldBe(ContextPriority.DecisiveClauses);
        Priority(items, "clause POL-7").ShouldBe(ContextPriority.DecisiveClauses);
        var other = items.Single(i => i.Key == "clause POL-8");
        other.Priority.ShouldBe(ContextPriority.OtherClauses);
        other.Score.ShouldBe(0.83, 0.0001);
        Priority(items, "global GLB-1").ShouldBe(ContextPriority.GlobalSnippets);
        items.Where(i => i.Key.StartsWith("evidence ", StringComparison.Ordinal)).ShouldAllBe(i => i.IsEvidence && i.IsRequired);
        items.Where(i => i.Priority == ContextPriority.CaseFacts).Select(i => i.Key)
            .ShouldBe(["case-facts", "description", "intake", "evidence EV-1", "evidence EV-2", "evidence EV-3", "policy-assessment", "coverage-window", "risk-signals"]);
    }

    [Fact]
    public async Task Over_budget_the_global_snippets_and_other_clauses_are_dropped_but_Period_and_Exclusion_clauses_kept()
    {
        _clauses.Clear();
        var references = new ReferenceRegistry();
        references.IssueEvidence(InvoiceId);
        references.IssueEvidence(FrontPhotoId);
        references.IssueEvidence(BackPhotoId);
        AddClause("2.1", ClauseType.Period, null, "Period clause. " + new string('p', 20_000), 0.5, references);
        AddClause("3.1", ClauseType.Exclusion, ExclusionCode.AccidentalDamage, "Exclusion clause. " + new string('e', 20_000), 0.4, references);
        AddClause("1.1", ClauseType.Coverage, null, "Coverage clause. " + new string('c', 60_000), 0.9, references);
        references.IssueChunk(new RetrievedChunk(Guid.NewGuid(), "global", Guid.NewGuid(), "Guide", 1, null, null, new string('g', 10_000), null, null, 0.9));
        _model.Enqueue(ScriptedModelProvider.Completed(Output(policyRefs: """[{"ref":"POL-1","relevance":"DEFINES_PERIOD"}]""")));

        var result = await RunAsync(Input(), references: references);

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var facts = string.Join('\n', _model.Requests.ShouldHaveSingleItem().Messages[0].Parts.OfType<TextPart>().Select(p => p.Text));
        facts.ShouldContain("Period clause.");
        facts.ShouldContain("Exclusion clause.");
        facts.ShouldNotContain("Coverage clause.");
        facts.ShouldNotContain("GLB-1");
        result.Diagnostics.ShouldContain(d => d.Contains("clause POL-3", StringComparison.Ordinal) && d.Contains("global GLB-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Required_context_over_the_budget_fails_without_a_model_call()
    {
        var input = Input() with { Case = Case() with { ProblemDescription = new string('x', 120_000) } };

        var result = await RunAsync(input);

        result.Status.ShouldBe(AgentStatus.Failed);
        result.Output.ShouldBeNull();
        result.Diagnostics.ShouldContain(d => d.StartsWith("context_overflow", StringComparison.Ordinal));
        _model.Requests.ShouldBeEmpty();
        _adjudication.DidNotReceive().AddRecommendation(Arg.Any<Recommendation>());
    }

    // ---- Invalid recommendations --------------------------------------------------------------------

    [Fact]
    public async Task Output_that_breaks_the_schema_is_stored_as_an_invalid_recommendation()
    {
        // The answer breaks the schema again in the one corrective turn.
        _model.Enqueue(ScriptedModelProvider.Completed(Output().Replace("\"APPROVE\"", "\"MAYBE\"", StringComparison.Ordinal)));
        _model.Enqueue(ScriptedModelProvider.Completed(Output().Replace("\"APPROVE\"", "\"MAYBE\"", StringComparison.Ordinal)));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        var recommendation = result.Output.ShouldNotBeNull().Recommendation;
        recommendation.IsValid.ShouldBeFalse();
        recommendation.ValidationErrors.ShouldNotBeEmpty();
        recommendation.Decision.ShouldBeNull();
        recommendation.Confidence.ShouldBe(93);
        recommendation.RawOutputJson.ShouldContain("MAYBE");
        recommendation.PromptId.ShouldBe("decision");
        result.Output.Risk.ShouldBe(AiRiskReading.None);
        _adjudication.Received(1).AddRecommendation(recommendation);
        _clauses.ShouldAllBe(c => !c.Cited);
    }

    [Fact]
    public async Task Output_that_breaks_a_description_rule_is_stored_as_invalid()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output(confidence: 150)));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        var recommendation = result.Output!.Recommendation;
        recommendation.IsValid.ShouldBeFalse();
        recommendation.ValidationErrors.ShouldContain(e => e.StartsWith("/confidence", StringComparison.Ordinal));
        recommendation.Decision.ShouldBe(AiDecision.Approve);
        recommendation.Confidence.ShouldBeNull();
    }

    [Theory]
    [InlineData("""[{"ref":"POL-99","relevance":"SUPPORTS_COVERAGE"}]""", "/policyRefs/0/ref: POL-99 was not issued for this run.")]
    [InlineData("""[{"ref":"POL-8","relevance":"SUPPORTS_COVERAGE"},{"ref":"GLB-1","relevance":"CONTEXT"}]""", "/policyRefs/1/ref: GLB-1 is not a POL-n reference.")]
    [InlineData("""[{"ref":"EV-1","relevance":"SUPPORTS_COVERAGE"}]""", "/policyRefs/0/ref: EV-1 is not a POL-n reference.")]
    public async Task A_policy_reference_that_was_not_issued_as_POL_n_makes_the_recommendation_invalid(string policyRefs, string error)
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output(policyRefs: policyRefs)));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        var recommendation = result.Output!.Recommendation;
        recommendation.IsValid.ShouldBeFalse();
        recommendation.ValidationErrors.ShouldContain(error);
        recommendation.Decision.ShouldBe(AiDecision.Approve);
        recommendation.Confidence.ShouldBe(93);
        _adjudication.Received(1).AddRecommendation(recommendation);
        _clauses.ShouldAllBe(c => !c.Cited);
    }

    [Fact]
    public async Task An_evidence_reference_that_was_not_issued_makes_the_recommendation_invalid()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output(evidenceRefs: """[{"ref":"EV-9","observation":"x"}]""")));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        result.Output!.Recommendation.ValidationErrors.ShouldContain("/evidenceRefs/0/ref: EV-9 was not issued for this run.");
    }

    [Fact]
    public async Task A_risk_signal_citing_an_unknown_evidence_reference_makes_the_recommendation_invalid()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output(risk: """{"level":"MEDIUM","signals":[{"code":"OTHER","description":"x","evidenceRefs":["POL-1"]}]}""")));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        result.Output!.Recommendation.ValidationErrors.ShouldContain("/risk/signals/0/evidenceRefs/0: POL-1 is not a EV-n reference.");
        result.Output.Risk.Signals.ShouldHaveSingleItem().EvidenceRefs.ShouldBeEmpty();
    }

    // ---- Model failures -----------------------------------------------------------------------------

    [Theory]
    [InlineData(AiStopKind.Refused, AgentStatus.Refused)]
    [InlineData(AiStopKind.Truncated, AgentStatus.Failed)]
    public async Task A_refused_or_truncated_turn_stores_no_recommendation(AiStopKind stop, AgentStatus expected)
    {
        _model.Enqueue(ScriptedModelProvider.Stopped(stop));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(expected);
        result.Output.ShouldBeNull();
        _adjudication.DidNotReceive().AddRecommendation(Arg.Any<Recommendation>());
    }

    [Theory]
    [InlineData(AiFailureKind.Timeout, AgentStatus.TimedOut)]
    [InlineData(AiFailureKind.ProviderError, AgentStatus.Failed)]
    public async Task A_failed_turn_maps_to_a_failing_status_instead_of_throwing(AiFailureKind kind, AgentStatus expected)
    {
        _model.Enqueue(ScriptedModelProvider.Failed(kind, "boom"));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(expected);
        result.Output.ShouldBeNull();
        result.Diagnostics.ShouldContain(d => d.Contains("boom", StringComparison.Ordinal));
        _adjudication.DidNotReceive().AddRecommendation(Arg.Any<Recommendation>());
    }

    [Fact]
    public async Task A_model_that_keeps_calling_tools_fails_after_six_turns()
    {
        for (var i = 0; i < 6; i++)
        {
            _model.Enqueue(ScriptedModelProvider.ToolCalls(new AiToolCall($"m{i}", ToolNames.ClaimHistoryLookup, Json("{}"))));
        }

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.Failed);
        _model.Requests.Count.ShouldBe(6);
        result.Output.ShouldBeNull();
    }

    [Fact]
    public async Task A_case_of_another_claim_is_rejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => RunAsync(Input() with { Case = Case() with { ClaimId = Guid.NewGuid() } }));
    }

    // ---- Redaction and risk -------------------------------------------------------------------------

    [Fact]
    public async Task The_stored_output_and_texts_are_redacted()
    {
        const string email = "philippa.quarrington@privacy-probe.test";
        const string phone = "+1 (415) 555-0137";
        _model.Enqueue(ScriptedModelProvider.Completed(Output(explanation: $"Your tablet is covered. Questions? Write to {email} or call {phone}.")));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var recommendation = result.Output!.Recommendation;
        foreach (var stored in new[] { recommendation.RawOutputJson, recommendation.ClaimantExplanation })
        {
            stored.ShouldNotContain(email);
            stored.ShouldNotContain("555-0137");
            stored.ShouldContain(CaseCustomerView.EmailPlaceholder);
            stored.ShouldContain(CaseCustomerView.PhonePlaceholder);
        }

        JsonDocument.Parse(recommendation.RawOutputJson).RootElement.GetProperty("confidence").GetInt32().ShouldBe(93);
    }

    [Fact]
    public async Task The_raw_output_of_a_schema_invalid_answer_is_redacted_too()
    {
        // The answer breaks the schema again in the one corrective turn.
        _model.Enqueue(ScriptedModelProvider.Completed("""{"decision":"APPROVE","note":"mail someone@privacy-probe.test"}"""));
        _model.Enqueue(ScriptedModelProvider.Completed("""{"decision":"APPROVE","note":"mail someone@privacy-probe.test"}"""));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        var raw = result.Output!.Recommendation.RawOutputJson;
        raw.ShouldNotContain("someone@privacy-probe.test");
        raw.ShouldContain(CaseCustomerView.EmailPlaceholder);
    }

    [Fact]
    public async Task The_model_risk_reading_is_exposed_as_AI_signals()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(Output(
            decision: "HUMAN_REVIEW",
            risk: """{"level":"HIGH","signals":[{"code":"DAMAGE_INCONSISTENT_WITH_DESCRIPTION","description":"The photo shows a cracked screen.","evidenceRefs":["EV-2","EV-2"]}]}""",
            manipulation: true)));

        var result = await RunAsync(Input());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var risk = result.Output!.Risk;
        risk.ModelLevel.ShouldBe(RiskLevel.High);
        risk.Signals.Select(s => s.Code).ShouldBe([RiskSignalCode.DamageInconsistentWithDescription, RiskSignalCode.ManipulationAttempt]);
        risk.Signals.ShouldAllBe(s => s.Source == RiskSignalSource.Ai && s.Severity == RiskSeverity.Medium);
        risk.Signals[0].EvidenceRefs.ShouldBe(["EV-2"]);
        result.Output.Recommendation.ManipulationDetected.ShouldBeTrue();
    }

    // ---- Replay -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_S1_decision_fixture_replays_through_the_agent()
    {
        var replay = new ReplayModelProvider(
            new FixedScenario("S1"),
            new ReplayCallCounter(),
            Options.Create(new AiGatewayOptions { Mode = AiGatewayOptions.ReplayMode }),
            NullLogger<ReplayModelProvider>.Instance);

        var result = await RunAsync(Input(), replay, AiGatewayOptions.ReplayMode);

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var recommendation = result.Output!.Recommendation;
        recommendation.IsValid.ShouldBeTrue();
        recommendation.Decision.ShouldBe(AiDecision.Approve);
        recommendation.Coverage.ShouldBe(CoverageDetermination.Covered);
        recommendation.Confidence.ShouldBe(93);
        recommendation.EvidenceRefs.Select(r => r.Ref).ShouldBe(["EV-1", "EV-2", "EV-3"]);
        recommendation.PolicyRefs.Select(r => r.Ref).ShouldBe(["POL-8", "POL-1"]);
        result.Output.Risk.Signals.ShouldBeEmpty();
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private Task<AgentResult<RecommendationResult>> RunAsync(DecisionInput input, ReferenceRegistry? references = null)
        => RunAsync(input, _model, AiGatewayOptions.LiveMode, references);

    private async Task<AgentResult<RecommendationResult>> RunAsync(DecisionInput input, IModelProvider provider, string mode, ReferenceRegistry? references = null)
    {
        _services = Gateway(provider, mode);
        await using var scope = _services.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IAiGateway>();
        var run = Run(references);
        var agent = new DecisionAgent(new AgentTurnLoop(), new ContextBuilder(), new SchemaValidator(), _adjudication, new RegexPiiRedactor());

        return await agent.RunAsync(input, new AgentExecutionContext(run, gateway, _tools, new ActivityTraceWriter()), TestContext.Current.CancellationToken);
    }

    private AdjudicationContext Run(ReferenceRegistry? references = null) => new(RunId, new FakeTenantContext(Aurora), Case(), references ?? _references);

    /// <summary>The real gateway (templates, schema validation, redaction) in front of <paramref name="provider"/>.</summary>
    private static ServiceProvider Gateway(IModelProvider provider, string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiGateway:Mode"] = mode,
            ["AiGateway:Routes:adjudication:Provider"] = AnthropicModelProvider.ProviderName,
            ["AiGateway:Routes:adjudication:Model"] = "claude-opus-4-1",
            ["AiGateway:Routes:adjudication:MaxTokens"] = "4000",
            ["AiGateway:Routes:adjudication:TimeoutSeconds"] = "60",
            ["AiGateway:Routes:embedding:Provider"] = HashEmbeddingGenerator.ProviderName,
            ["AiGateway:Routes:embedding:Model"] = "hash",
            ["AiGateway:Routes:embedding:Dimensions"] = "768",
            ["AiGateway:Anthropic:NoTraining"] = "true",
            ["AiGateway:RateLimits:PerTenantRequestsPerMinute"] = "600",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Aurora));
        services.AddScoped(_ => Substitute.For<IAiOpsRepository>());
        services.AddScoped(_ => Substitute.For<IClaimRepository>());
        services.AddWarrantyAiGateway(configuration);
        services.RemoveAll<IModelProvider>();
        services.AddSingleton(provider);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private DecisionInput Input(IReadOnlyList<RiskSignal>? signals = null)
    {
        var intake = IntakeResult.Create(RunId, Aurora, [new ValidationCheck("REQUIRED_FIELDS", true, null)], Extraction, []);
        EvidenceFinding[] findings =
        [
            EvidenceFinding.Create(
                Guid.NewGuid(), Aurora, RunId, InvoiceId, EvidenceFindingKind.InvoiceExtraction, InvoiceFinding,
                [new ConsistencyCheck("serialNumber", Serial, Serial, true), new ConsistencyCheck("seller", "Aurora Store", "Aurora Store", true)], null),
            EvidenceFinding.Create(
                Guid.NewGuid(), Aurora, RunId, BackPhotoId, EvidenceFindingKind.PhotoAnalysis, PhotoFinding,
                [new ConsistencyCheck("serialNumber", Serial, Serial, true)], 92),
        ];
        var assessment = PolicyAssessment.Create(RunId, Aurora, PolicyVersionOutcome.Ok, Assessment, 92, "claude-sonnet-4-5", "1");
        return new DecisionInput(
            Case(), intake, findings, [], PolicyVersionOutcome.Ok, assessment, _clauses,
            new CoverageWindowResult(CoverageWindowOutcome.Determined, new DateOnly(2027, 6, 4), true, true), signals ?? []);
    }

    private static CaseContext Case() => new(
        ClaimId, 1, "WC-2026-000002", ClaimChannel.ClaimantPortal, Today, PurchaseDate, "Aurora Store", 450m, "USD", Region.NA, ModelCode, Serial,
        Description, new CaseProduct(Guid.NewGuid(), ModelCode, "Aurora Tab 10", "tablet", 450m), new CaseCustomerView("US", Region.NA),
        [Evidence(InvoiceId, EvidenceKind.Invoice, "application/pdf"), Evidence(FrontPhotoId, EvidenceKind.Photo, "image/jpeg"), Evidence(BackPhotoId, EvidenceKind.Photo, "image/jpeg")],
        ClaimHistoryCounts.None, ReviewerInfoRequested: false, AutoInfoRequestCount: 0);

    private static CaseEvidence Evidence(Guid id, EvidenceKind kind, string contentType)
        => new(id, kind, kind == EvidenceKind.Invoice ? "invoice.pdf" : "photo.jpg", contentType, 2_048, new string('a', 64), 1, $"claims/{ClaimId}/1/{id}");

    private void AddClause(string key, ClauseType type, ExclusionCode? exclusion, string text, double score, ReferenceRegistry? references = null)
    {
        var chunk = new RetrievedChunk(
            Guid.NewGuid(), $"tenant:{Aurora}", Guid.NewGuid(), "Aurora Limited Warranty", 2, key, null, text, new DateOnly(2026, 1, 1), null, score,
            Aurora, PolicyVersionId, type, exclusion);
        var refId = (references ?? _references).IssueChunk(chunk);
        _clauses.Add(RetrievedPolicyRef.Create(
            Guid.NewGuid(), Aurora, RunId, refId, chunk.ChunkId, PolicyVersionId, key, type, exclusion, chunk.DocumentTitle, 2,
            new DateOnly(2026, 1, 1), null, (float)score));
    }

    private static RiskSignal Signal(RiskSignalCode code)
        => new(code, RiskSignalSource.Deterministic, RiskSeverity.Medium, "1 other claim(s) for this serial number are open.", []);

    private static string Output(
        string decision = "APPROVE",
        int confidence = 93,
        string risk = """{"level":"LOW","signals":[]}""",
        string evidenceRefs = """[{"ref":"EV-1","observation":"Invoice matches."},{"ref":"EV-3","observation":"Serial label matches."}]""",
        string policyRefs = """[{"ref":"POL-8","relevance":"SUPPORTS_COVERAGE"},{"ref":"POL-1","relevance":"DEFINES_PERIOD"}]""",
        string explanation = "Your tablet is covered by the warranty and will be repaired.",
        bool manipulation = false)
        => $$"""
            {"decision":"{{decision}}","coverage":"COVERED","confidence":{{confidence}},"risk":{{risk}},"evidenceRefs":{{evidenceRefs}},
             "policyRefs":{{policyRefs}},"missingInformation":[],"reasoningSummary":"Covered manufacturing defect (EV-1, EV-3, POL-8, POL-1).",
             "claimantExplanation":{{JsonSerializer.Serialize(explanation)}},"manipulationDetected":{{(manipulation ? "true" : "false")}}}
            """;

    private static ContextPriority Priority(IReadOnlyList<ContextItem> items, string key) => items.Single(i => i.Key == key).Priority;

    private static IEnumerable<string> Texts(ResolvedTurnRequest request)
        => request.Messages.SelectMany(m => m.Parts).SelectMany(TextsOf).Append(request.SystemPrompt);

    private static IEnumerable<string> TextsOf(AiContentPart part) => part switch
    {
        TextPart text => [text.Text],
        UntrustedTextPart untrusted => [untrusted.Label, untrusted.Text],
        ToolResultPart result => [result.Result.GetRawText()],
        ToolCallPart call => [call.Arguments.GetRawText()],
        _ => [],
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The decision tools with fixed answers; records every call.</summary>
    private sealed class FakeTools : IToolInvoker
    {
        private readonly List<AiToolCall> _calls = [];

        public ClaimHistoryCounts History { get; set; } = ClaimHistoryCounts.None;

        public IReadOnlyList<AiToolCall> Calls => _calls;

        public IReadOnlyList<AiToolDefinition> Definitions { get; } =
        [
            new(ToolNames.ClaimHistoryLookup, "Claim history counts.", Json("""{"type":"object","additionalProperties":false,"properties":{}}""")),
            new(ToolNames.SearchGlobalKnowledge, "Global knowledge.", Json("""{"type":"object","additionalProperties":false,"properties":{"query":{"type":"string"}},"required":["query"]}""")),
        ];

        public Task<AiToolResult> InvokeAsync(AiToolCall call, CancellationToken ct)
        {
            _calls.Add(call);
            object content = call.ToolName switch
            {
                ToolNames.ClaimHistoryLookup => History,
                ToolNames.SearchGlobalKnowledge => new { results = Array.Empty<object>() },
                _ => new { error = $"Tool '{call.ToolName}' is not available." },
            };
            var isError = call.ToolName is not (ToolNames.ClaimHistoryLookup or ToolNames.SearchGlobalKnowledge);
            return Task.FromResult(new AiToolResult(call.CallId, JsonSerializer.SerializeToElement(content, ToolJson.Options), isError));
        }
    }

    private sealed class FixedScenario(string scenario) : IReplayScenarioSelector
    {
        public Task<string?> SelectScenarioAsync(AiCallContext context, CancellationToken ct) => Task.FromResult<string?>(scenario);
    }
}
