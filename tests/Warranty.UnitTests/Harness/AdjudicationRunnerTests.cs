using NSubstitute;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Tools;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Domain.Tenancy;
using Warranty.Guardrails;
using Warranty.Guardrails.Rules;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Harness;

/// <summary>
/// The run lifecycle (contracts/agents-and-tools.md "Harness lifecycle"): step order, checkpoints and resume,
/// the intake short-circuit, AI failures as outcomes, infrastructure failures as exceptions, one risk
/// assessment per run, trail entries and spans. Agents and the risk capability are fakes; the guardrail
/// engine is the real one, so the stored evaluation is what production would store.
/// </summary>
public sealed class AdjudicationRunnerTests
{
    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000c8");
    private static readonly Guid CustomerId = Guid.Parse("0199a000-0000-7000-8000-0000000000d8");
    private static readonly Guid ProductId = Guid.Parse("0199a000-0000-7000-8000-0000000000e8");
    private static readonly Guid PolicyId = Guid.Parse("0199a000-0000-7000-8000-0000000000f8");
    private static readonly Guid VersionId = Guid.Parse("0199a000-0000-7000-8000-0000000000f9");
    private static readonly Guid InvoiceId = Guid.Parse("0199a000-0000-7000-8000-0000000000a1");
    private static readonly Guid PhotoId = Guid.Parse("0199a000-0000-7000-8000-0000000000a2");
    private static readonly Guid PeriodChunk = Guid.Parse("0199a000-0000-7000-8000-0000000000b1");
    private static readonly Guid CoverageChunk = Guid.Parse("0199a000-0000-7000-8000-0000000000b2");
    private static readonly DateOnly PurchaseDate = new(2026, 6, 4);
    private static readonly DateOnly ClaimDate = new(2026, 10, 4);

    private const string Extraction = """
        {"problemCategory":"POWER_FAILURE","component":"MAINBOARD","symptoms":["does not power on"],"claimedCause":"SPONTANEOUS_FAILURE",
         "mentionsAccident":false,"mentionsLiquid":false,"containsInstructionsToSystem":false,"summary":"The tablet does not power on."}
        """;

    private const string InvoiceJson = """
        {"evidenceRef":"EV-1","legible":true,"sellerName":"Aurora Store","invoiceNumber":"INV-1","invoiceDate":"2026-06-04",
         "productDescription":"Aurora Tab 10","modelCodeOnInvoice":"AUR-TAB10","serialOnInvoice":"AT10-TEST-1","totalAmount":450.0,
         "currency":"USD","anomalies":[],"containsInstructionsToSystem":false}
        """;

    private const string PhotoJson = """
        {"evidenceRef":"EV-2","showsProduct":true,"productTypeObserved":"tablet","visibleSerial":"NOT_VISIBLE","damageObserved":false,
         "damageTypes":["NONE_VISIBLE"],"consistentWithDescription":"CONSISTENT","imageQuality":"GOOD","containsInstructionsToSystem":false,
         "confidence":90,"observations":"A tablet with a black screen."}
        """;

    private readonly List<string> _log = [];
    private readonly FakeStore _store = new();
    private readonly FakeTrail _trail = new();
    private readonly FakeTraces _traces = new();
    private readonly IActionExecutor _actions = Substitute.For<IActionExecutor>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly PolicyVersion _version;
    private readonly Claim _claim;

    private Func<AgentResult<IntakeResult>, AgentResult<IntakeResult>> _intakeOutcome = r => r;
    private Func<AgentResult<EvidenceResult>, AgentResult<EvidenceResult>> _evidenceOutcome = r => r;
    private Func<AgentResult<RecommendationResult>, AgentResult<RecommendationResult>> _decisionOutcome = r => r;
    private bool _missingItems;
    private int _detectFailures;
    private Action<DecisionInput, AgentExecutionContext>? _onDecision;
    private CaseProduct? _caseProduct = new(ProductId, "AUR-TAB10", "Aurora Tab 10", "tablet", 450m);

    public AdjudicationRunnerTests()
    {
        var terms = new CoverageTerms(new Dictionary<Region, int> { [Region.NA] = 12 }, new Dictionary<string, int>(), AccidentalDamageTerms.NotCovered, []);
        _version = PolicyVersion.Create(VersionId, Aurora, PolicyId, 1, new DateOnly(2026, 1, 1), null, [Region.NA], ["tablet"], terms, "policies/aur.md", "checksum");
        _claim = Claim.Submit(
            ClaimId, Aurora, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, CustomerId,
            "claimant@example.test", null, "AUR-TAB10", ProductId, "AT10-TEST-1", PurchaseDate, "Aurora Store", 450m, Region.NA,
            "The tablet does not power on.", new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));
        _actions.ExecuteAsync(default!, default).ReturnsForAnyArgs(ActionExecution.Executed);
        _policies.GetVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_version);
        _policies.GetClausesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(
        [
            PolicyClause.Create(Guid.NewGuid(), _version, "AUR-WP-2.1", ClauseType.Period, null, "Warranty period", "Defects are covered for 12 months."),
            PolicyClause.Create(Guid.NewGuid(), _version, "AUR-WP-1.1", ClauseType.Coverage, null, "Coverage", "Manufacturing defects are covered."),
        ]);
    }

    [Fact]
    public async Task A_clear_claim_runs_every_step_in_order_and_is_finalized_through_the_guardrails()
    {
        await Runner().RunAsync(ClaimId, 1, Ct);

        _log.ShouldBe(["intake", "evidence", "policy", "risk:detect", "decision", "risk:full"]);
        var run = _store.Runs.ShouldHaveSingleItem();
        run.Status.ShouldBe(RunStatus.Completed);
        run.CurrentStep.ShouldBe(RunStep.Done);
        run.Disposition.ShouldBe(Disposition.AutoApprove);
        run.FailureReason.ShouldBeNull();
        run.ReferenceMap.Keys.ShouldBe(["EV-1", "EV-2", "POL-1", "POL-2"], ignoreOrder: true);
        _claim.Status.ShouldBe(ClaimStatus.UnderEvaluation, "the action executor (a fake here) moves the claim on");

        var evaluation = _store.Committed<GuardrailEvaluation>().ShouldHaveSingleItem();
        evaluation.Disposition.ShouldBe(Disposition.AutoApprove);
        evaluation.Checks.ShouldAllBe(c => c.Passed);
        evaluation.ApprovedActionJson!.ShouldContain("FinalizeApproved");
        await _actions.Received(1).ExecuteAsync(Arg.Is<ApprovedAction>(a => a.Kind == ActionKind.FinalizeApproved && a.RunId == run.Id), Arg.Any<CancellationToken>());

        _store.Committed<RiskAssessment>().ShouldHaveSingleItem().Stage.ShouldBe(RiskAssessmentStage.Full);
        _store.Committed<RetrievedPolicyRef>().Single(r => r.RefId == "POL-2").Cited.ShouldBeTrue();
        _trail.Steps.ShouldBe(
        [
            TrailStep.IntakeValidated, TrailStep.ClaimExtracted, TrailStep.CustomerVerified, TrailStep.ProductIdentified,
            TrailStep.EvidenceAnalyzed, TrailStep.PolicyRetrieved, TrailStep.CoverageAssessed, TrailStep.RiskEvaluated,
            TrailStep.AiRecommended, TrailStep.GuardrailsEvaluated,
        ]);
    }

    [Fact]
    public async Task A_case_not_in_the_catalog_reaches_the_guardrails_without_a_claim_value_and_goes_to_human_review()
    {
        _caseProduct = null;

        await Runner().RunAsync(ClaimId, 1, Ct);

        var evaluation = _store.Committed<GuardrailEvaluation>().ShouldHaveSingleItem();
        evaluation.Disposition.ShouldBe(Disposition.HumanReview, "the AI recommends APPROVE, yet a claim without a catalog product is never decided automatically");
        var catalog = evaluation.Checks.Single(c => c.Code == GuardrailCheckCode.ProductInCatalog);
        var value = evaluation.Checks.Single(c => c.Code == GuardrailCheckCode.ClaimValueWithinLimit);
        (catalog.Passed, value.Passed, value.Actual).ShouldBe((false, false, "unknown"));
        evaluation.Reasons.ShouldContain(EscalationReason.ProductNotInCatalog);
        evaluation.ApprovedActionJson!.ShouldContain("EscalateToReview");
        await _actions.DidNotReceive().ExecuteAsync(Arg.Is<ApprovedAction>(a => a.Kind == ActionKind.FinalizeApproved), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_step_runs_in_its_own_span_on_the_harness_activity_source()
    {
        await Runner().RunAsync(ClaimId, 1, Ct);

        _traces.Spans.ShouldBe(
        [
            "harness.run", "harness.step.load_case", "harness.step.intake", "harness.step.evidence", "harness.step.policy", "harness.step.risk",
            "harness.step.decision", "harness.step.guardrails", "harness.step.action",
        ]);
        ActivityTraceWriter.SourceName.ShouldBe("Warranty.AI.Harness");
    }

    [Fact]
    public async Task Every_step_commits_its_rows_with_the_next_checkpoint()
    {
        await Runner().RunAsync(ClaimId, 1, Ct);

        _store.Checkpoints.ShouldBe([RunStep.Intake, RunStep.Evidence, RunStep.Policy, RunStep.Risk, RunStep.Decision, RunStep.Guardrails, RunStep.Done]);
    }

    [Fact]
    public async Task An_infrastructure_failure_propagates_and_the_retried_job_resumes_after_the_last_completed_step()
    {
        _detectFailures = 1;

        var failure = await Should.ThrowAsync<TimeoutException>(() => Runner().RunAsync(ClaimId, 1, Ct));

        failure.Message.ShouldBe("database timeout");
        var run = _store.Runs.ShouldHaveSingleItem();
        run.Status.ShouldBe(RunStatus.Running);
        run.CurrentStep.ShouldBe(RunStep.Risk);
        _store.Rollback();

        DecisionInput? seen = null;
        ReferenceEntry? coverage = null;
        _onDecision = (input, ctx) =>
        {
            seen = input;
            ctx.Run.References.TryResolve("POL-2", out var entry);
            coverage = entry;
        };
        await Runner().RunAsync(ClaimId, 1, Ct);

        _log.ShouldBe(["intake", "evidence", "policy", "risk:detect!", "risk:detect", "decision", "risk:full"]);
        run.Status.ShouldBe(RunStatus.Completed);
        seen!.EvidenceFindings.Count.ShouldBe(2);
        seen.PolicyVersionOutcome.ShouldBe(PolicyVersionOutcome.Ok);
        seen.CoverageWindow.WithinComponentCoverage.ShouldBe(true);
        seen.PolicyClauses.ShouldBe(_store.Committed<RetrievedPolicyRef>(), "the resumed run cites the tracked clause rows");
        coverage!.Chunk!.Text.ShouldBe("Manufacturing defects are covered.");
        _store.Committed<IntakeResult>().ShouldHaveSingleItem();
        _store.Committed<RiskAssessment>().ShouldHaveSingleItem();
        _trail.Steps.Count(s => s == TrailStep.IntakeValidated).ShouldBe(1);
        _trail.Steps[^1].ShouldBe(TrailStep.GuardrailsEvaluated);
        run.Disposition.ShouldBe(Disposition.AutoApprove);
    }

    [Fact]
    public async Task A_completed_run_is_not_run_again()
    {
        await Runner().RunAsync(ClaimId, 1, Ct);
        _log.Clear();

        await Runner().RunAsync(ClaimId, 1, Ct);

        _log.ShouldBeEmpty();
        await _actions.Received(1).ExecuteAsync(Arg.Any<ApprovedAction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_items_short_circuit_to_a_request_for_information_without_the_ai_analysis_steps()
    {
        _missingItems = true;

        await Runner().RunAsync(ClaimId, 1, Ct);

        _log.ShouldBe(["intake", "risk:intake"]);
        var run = _store.Runs.ShouldHaveSingleItem();
        run.Disposition.ShouldBe(Disposition.RequestInformation);
        _store.Committed<RiskAssessment>().ShouldHaveSingleItem().Stage.ShouldBe(RiskAssessmentStage.Intake);
        await _actions.Received(1).ExecuteAsync(
            Arg.Is<ApprovedAction>(a => a.Kind == ActionKind.RequestInformation && a.RequestedItems.Any(i => i.Item == RequestedItemCodes.Invoice)),
            Arg.Any<CancellationToken>());
        _trail.Steps.ShouldBe(
        [
            TrailStep.IntakeValidated, TrailStep.ClaimExtracted, TrailStep.CustomerVerified, TrailStep.ProductIdentified, TrailStep.RiskEvaluated,
            TrailStep.GuardrailsEvaluated,
        ]);
    }

    [Fact]
    public async Task A_failed_decision_routes_the_claim_to_human_review_with_the_failure_recorded()
    {
        _decisionOutcome = _ => AgentResult<RecommendationResult>.Failure(AgentStatus.TimedOut, "Timeout: the model did not answer in time.");

        await Runner().RunAsync(ClaimId, 1, Ct);

        var run = _store.Runs.ShouldHaveSingleItem();
        run.Status.ShouldBe(RunStatus.Completed);
        run.Disposition.ShouldBe(Disposition.HumanReview);
        run.FailureReason.ShouldBe("decision: TimedOut: Timeout: the model did not answer in time.");
        var evaluation = _store.Committed<GuardrailEvaluation>().ShouldHaveSingleItem();
        evaluation.Reasons.ShouldContain(EscalationReason.AiUnavailable);
        _store.Committed<RiskAssessment>().ShouldHaveSingleItem().Stage.ShouldBe(RiskAssessmentStage.Full);
        await _actions.Received(1).ExecuteAsync(Arg.Is<ApprovedAction>(a => a.Kind == ActionKind.EscalateToReview), Arg.Any<CancellationToken>());
        _trail.Steps.ShouldBe(
        [
            TrailStep.IntakeValidated, TrailStep.ClaimExtracted, TrailStep.CustomerVerified, TrailStep.ProductIdentified,
            TrailStep.EvidenceAnalyzed, TrailStep.PolicyRetrieved, TrailStep.CoverageAssessed, TrailStep.RiskEvaluated,
            TrailStep.AiStepFailed, TrailStep.GuardrailsEvaluated,
        ]);
    }

    [Fact]
    public async Task A_partial_evidence_failure_skips_the_decision_and_escalates()
    {
        _evidenceOutcome = r => r with { Status = AgentStatus.Refused, Diagnostics = ["EV-2: the model refused."] };

        await Runner().RunAsync(ClaimId, 1, Ct);

        _log.ShouldBe(["intake", "evidence", "policy", "risk:detect", "risk:full"]);
        var run = _store.Runs.ShouldHaveSingleItem();
        run.Disposition.ShouldBe(Disposition.HumanReview);
        run.FailureReason.ShouldBe("evidence: Refused: EV-2: the model refused.");
        var evaluation = _store.Committed<GuardrailEvaluation>().ShouldHaveSingleItem();
        evaluation.Checks.Single(c => c.Code == GuardrailCheckCode.NoConflicts).Passed.ShouldBeFalse();
        evaluation.Reasons.ShouldContain(EscalationReason.AiUnavailable);
        _trail.Steps.ShouldContain(TrailStep.AiStepFailed);
        _trail.Steps.ShouldNotContain(TrailStep.AiRecommended);
    }

    [Fact]
    public async Task A_failed_intake_extraction_skips_the_analysis_and_escalates()
    {
        _intakeOutcome = r => r with { Status = AgentStatus.InvalidOutput, Diagnostics = ["/component: not in the enum."] };

        await Runner().RunAsync(ClaimId, 1, Ct);

        _log.ShouldBe(["intake", "risk:detect", "risk:full"]);
        _store.Runs.Single().Disposition.ShouldBe(Disposition.HumanReview);
        _store.Committed<GuardrailEvaluation>().Single().Reasons.ShouldContain(EscalationReason.AiUnavailable);
    }

    [Fact]
    public void The_failure_reason_lists_one_failed_step_per_line_and_reads_back()
    {
        var failures = new[]
        {
            new AiStepFailure(AgentNames.Evidence, AgentStatus.TimedOut, "EV-2: no answer\nafter 30 s"),
            new AiStepFailure(AgentNames.Decision, AgentStatus.InvalidOutput, "/decision: missing"),
        };

        var text = AiStepFailure.Format(failures);

        text.ShouldBe("evidence: TimedOut: EV-2: no answer after 30 s\ndecision: InvalidOutput: /decision: missing");
        AiStepFailure.Parse(text).Select(f => (f.Agent, f.Status)).ShouldBe([(AgentNames.Evidence, AgentStatus.TimedOut), (AgentNames.Decision, AgentStatus.InvalidOutput)]);
        AiStepFailure.Parse("not a failure line").ShouldBeEmpty();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdjudicationRunner Runner()
    {
        var tenant = new FakeTenantContext(Aurora);
        var cases = Substitute.For<ICaseKnowledgeProvider>();
        cases.GetCaseContextAsync(ClaimId, 1, Arg.Any<CancellationToken>()).Returns(Case() with { Product = _caseProduct });
        var claims = Substitute.For<IClaimRepository>();
        claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(_claim);
        var tenants = Substitute.For<ITenantRepository>();
        tenants.GetCurrentSettingsAsync(Arg.Any<CancellationToken>()).Returns(TenantSettings.Create(Aurora, "USD", 500m, 80));
        var redactor = Substitute.For<IPiiRedactor>();
        redactor.Redact(Arg.Any<string>()).Returns(ci => new RedactionResult(ci.Arg<string>(), 0));
        var tools = new ToolInvoker(
            new ToolRegistry([]), Substitute.For<IAiOpsRepository>(), Substitute.For<ISecurityEventWriter>(), redactor, TimeProvider.System);

        return new AdjudicationRunner(
            tenant, cases, claims, _store, _policies, tenants, _store, _trail,
            new AdjudicationAgents(
                new FakeAgent<CaseContext, IntakeResult>(AgentNames.Intake, Intake),
                new FakeAgent<EvidenceInput, EvidenceResult>(AgentNames.Evidence, Evidence),
                new FakeAgent<PolicyInput, PolicyResult>(AgentNames.Policy, Policy),
                new FakeAgent<DecisionInput, RecommendationResult>(AgentNames.Decision, Decision)),
            new FakeRisk(this), new GuardrailEngine(), _actions, Substitute.For<IAiGateway>(), tools, _traces, redactor, TimeProvider.System);
    }

    private AgentResult<IntakeResult> Intake(CaseContext input, AgentExecutionContext ctx)
    {
        _log.Add("intake");
        IReadOnlyList<RequestedItem> missing = _missingItems ? [RequestedItem.Create(RequestedItemCodes.Invoice, "Please add the invoice.")] : [];
        var result = IntakeResult.Create(
            ctx.Run.RunId, Aurora,
            [new ValidationCheck("REQUIRED_FIELDS", true, null), new ValidationCheck("INVOICE_PRESENT", !_missingItems, null), new ValidationCheck("REGION_DETERMINED", true, "NA")],
            Extraction, missing);
        _store.AddIntakeResult(result);
        return _intakeOutcome(AgentResult<IntakeResult>.Success(result));
    }

    private AgentResult<EvidenceResult> Evidence(EvidenceInput input, AgentExecutionContext ctx)
    {
        _log.Add("evidence");
        var invoiceRef = ctx.Run.References.IssueEvidence(InvoiceId);
        ctx.Run.References.IssueEvidence(PhotoId);
        var invoice = EvidenceFinding.Create(
            Guid.NewGuid(), Aurora, ctx.Run.RunId, InvoiceId, EvidenceFindingKind.InvoiceExtraction, InvoiceJson,
            [new ConsistencyCheck("serialNumber", "AT10-TEST-1", "AT10-TEST-1", true)], null);
        var photo = EvidenceFinding.Create(Guid.NewGuid(), Aurora, ctx.Run.RunId, PhotoId, EvidenceFindingKind.PhotoAnalysis, PhotoJson, [], 90);
        _store.AddEvidenceFinding(invoice);
        _store.AddEvidenceFinding(photo);
        var result = new EvidenceResult([invoice, photo], [.. invoice.Consistency], [])
        {
            Validation = [new ValidationCheck(ValidationCheckCodes.InvoiceLegible, true, $"Legible: {invoiceRef}.")],
        };
        return _evidenceOutcome(AgentResult<EvidenceResult>.Success(result));
    }

    private AgentResult<PolicyResult> Policy(PolicyInput input, AgentExecutionContext ctx)
    {
        _log.Add("policy");
        var clauses = new List<RetrievedPolicyRef>();
        foreach (var (chunkId, key, type) in new[] { (PeriodChunk, "AUR-WP-2.1", ClauseType.Period), (CoverageChunk, "AUR-WP-1.1", ClauseType.Coverage) })
        {
            var chunk = new RetrievedChunk(
                chunkId, "tenant-aurora", Guid.NewGuid(), "Aurora Limited Warranty", 1, key, key, $"Text of {key}.", _version.EffectiveFrom, null, 0.8,
                Aurora, VersionId, type);
            var refId = ctx.Run.References.IssueChunk(chunk);
            var reference = RetrievedPolicyRef.Create(
                Guid.NewGuid(), Aurora, ctx.Run.RunId, refId, chunkId, VersionId, key, type, null, "Aurora Limited Warranty", 1, _version.EffectiveFrom, null, 0.8f);
            _store.AddRetrievedPolicyRef(reference);
            clauses.Add(reference);
        }

        var assessment = PolicyAssessment.Create(
            ctx.Run.RunId, Aurora, PolicyVersionOutcome.Ok,
            """{"coverageAssessment":"COVERED","ambiguity":{"isAmbiguous":false,"explanation":""}}""", 90, "model", "policy.v1");
        _store.AddPolicyAssessment(assessment);
        var window = CoverageWindowCalculator.Calculate(_version.Terms, Region.NA, "mainboard", PurchaseDate, ClaimDate);
        return AgentResult<PolicyResult>.Success(new PolicyResult(RetrievalOutcome.Ok, clauses, assessment, _version)
        {
            CoverageWindow = window,
            Component = "MAINBOARD",
            CoverageAssessment = "COVERED",
        });
    }

    private AgentResult<RecommendationResult> Decision(DecisionInput input, AgentExecutionContext ctx)
    {
        _log.Add("decision");
        _onDecision?.Invoke(input, ctx);
        var recommendation = Recommendation.CreateValid(
            ctx.Run.RunId, Aurora, "{}", AiDecision.Approve, CoverageDetermination.Covered, 90, "A covered manufacturing defect.",
            "Your tablet is covered by the warranty and will be repaired.", [new EvidenceCitation("EV-1", "Matching invoice.")],
            [new PolicyCitation("POL-2", PolicyRefRelevance.SupportsCoverage), new PolicyCitation("POL-1", PolicyRefRelevance.DefinesPeriod)],
            [], false, "model", "decision", "1");
        _store.AddRecommendation(recommendation);
        foreach (var clause in input.PolicyClauses.Where(c => c.RefId == "POL-2"))
        {
            clause.MarkCited();
        }

        return _decisionOutcome(AgentResult<RecommendationResult>.Success(new RecommendationResult(recommendation, new AiRiskReading(RiskLevel.Low, []))));
    }

    private static CaseContext Case() => new(
        ClaimId, 1, "REFTEST001", ClaimChannel.ClaimantPortal, ClaimDate, PurchaseDate, "Aurora Store", 450m, "USD", Region.NA, "AUR-TAB10",
        "AT10-TEST-1", "The tablet does not power on.", new CaseProduct(ProductId, "AUR-TAB10", "Aurora Tab 10", "tablet", 450m),
        new CaseCustomerView("US", Region.NA),
        [
            new CaseEvidence(InvoiceId, EvidenceKind.Invoice, "invoice.pdf", "application/pdf", 1000, "aa", 1, "claims/x/1/invoice.pdf"),
            new CaseEvidence(PhotoId, EvidenceKind.Photo, "photo.jpg", "image/jpeg", 1000, "bb", 1, "claims/x/1/photo.jpg"),
        ],
        ClaimHistoryCounts.None, false, 0);

    private sealed class FakeAgent<TIn, TOut>(string name, Func<TIn, AgentExecutionContext, AgentResult<TOut>> run) : IAgent<TIn, TOut>
    {
        public AgentDescriptor Descriptor { get; } = new(name, "route", new PromptRef(name, 1), null, [], 1, 1_000);

        public Task<AgentResult<TOut>> RunAsync(TIn input, AgentExecutionContext ctx, CancellationToken ct)
        {
            ctx.Tools.Definitions.ShouldBeEmpty();
            return Task.FromResult(run(input, ctx));
        }
    }

    private sealed class FakeRisk(AdjudicationRunnerTests test) : IRiskAssessor
    {
        public Task<RiskAssessment> AssessAtIntakeAsync(CaseContext @case, IntakeResult intake, CancellationToken ct)
        {
            test._log.Add("risk:intake");
            var assessment = RiskAssessment.Create(intake.RunId, Aurora, RiskAssessmentStage.Intake, 0, RiskLevel.Low, []);
            test._store.AddRiskAssessment(assessment);
            return Task.FromResult(assessment);
        }

        public Task<IReadOnlyList<RiskSignal>> DetectDeterministicSignalsAsync(AdjudicationContext run, CancellationToken ct)
        {
            if (test._detectFailures > 0)
            {
                test._detectFailures--;
                test._log.Add("risk:detect!");
                throw new TimeoutException("database timeout");
            }

            test._log.Add("risk:detect");
            return Task.FromResult<IReadOnlyList<RiskSignal>>([]);
        }

        public Task<RiskAssessment> AssessFullAsync(AdjudicationContext run, AiRiskReading ai, CancellationToken ct)
        {
            test._log.Add("risk:full");
            var assessment = RiskAssessment.Create(run.RunId, Aurora, RiskAssessmentStage.Full, 0, RiskLevel.Low, []);
            test._store.AddRiskAssessment(assessment);
            run.Risk = assessment;
            return Task.FromResult(assessment);
        }
    }

    /// <summary>
    /// The adjudication repository and unit of work: added rows stay pending until a save or transaction
    /// commits them; reads see committed rows only, and <see cref="Rollback"/> drops what a crashed scope left.
    /// </summary>
    private sealed class FakeStore : IAdjudicationRepository, IUnitOfWork
    {
        private readonly List<object> _pending = [];
        private readonly List<object> _committed = [];

        public List<AdjudicationRun> Runs => [.. _committed.OfType<AdjudicationRun>()];

        /// <summary>The run's checkpoint at each commit.</summary>
        public List<RunStep> Checkpoints { get; } = [];

        public IReadOnlyList<T> Committed<T>() => [.. _committed.OfType<T>()];

        public void Rollback() => _pending.Clear();

        public void AddRun(AdjudicationRun run) => _pending.Add(run);

        public Task<AdjudicationRun?> GetRunAsync(Guid claimId, int round, CancellationToken ct)
            => Task.FromResult(Runs.SingleOrDefault(r => r.ClaimId == claimId && r.Round == round));

        public Task<AdjudicationRun?> GetLatestRunAsync(Guid claimId, CancellationToken ct)
            => Task.FromResult(Runs.Where(r => r.ClaimId == claimId).MaxBy(r => r.Round));

        public void AddIntakeResult(IntakeResult result) => _pending.Add(result);

        public void AddEvidenceFinding(EvidenceFinding finding) => _pending.Add(finding);

        public void AddRetrievedPolicyRef(RetrievedPolicyRef reference) => _pending.Add(reference);

        public void AddPolicyAssessment(PolicyAssessment assessment) => _pending.Add(assessment);

        public void AddRiskAssessment(RiskAssessment assessment) => _pending.Add(assessment);

        public void AddRecommendation(Recommendation recommendation) => _pending.Add(recommendation);

        public void AddGuardrailEvaluation(GuardrailEvaluation evaluation) => _pending.Add(evaluation);

        public Task<RunRecord?> GetRunRecordAsync(Guid runId, CancellationToken ct)
        {
            var run = Runs.SingleOrDefault(r => r.Id == runId);
            return Task.FromResult(run is null
                ? null
                : new RunRecord(
                    run,
                    Committed<IntakeResult>().SingleOrDefault(i => i.RunId == runId),
                    [.. Committed<EvidenceFinding>().Where(f => f.RunId == runId)],
                    [.. Committed<RetrievedPolicyRef>().Where(r => r.RunId == runId)],
                    Committed<PolicyAssessment>().SingleOrDefault(a => a.RunId == runId),
                    Committed<RiskAssessment>().SingleOrDefault(r => r.RunId == runId),
                    Committed<Recommendation>().SingleOrDefault(r => r.RunId == runId),
                    Committed<GuardrailEvaluation>().SingleOrDefault(g => g.RunId == runId)));
        }

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Commit();
            return Task.CompletedTask;
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct)
        {
            await work(ct);
            Commit();
        }

        private void Commit()
        {
            _committed.AddRange(_pending);
            _pending.Clear();
            if (Runs.SingleOrDefault() is { } run)
            {
                Checkpoints.Add(run.CurrentStep);
            }
        }
    }

    private sealed class FakeTrail : IDecisionTrailWriter
    {
        public List<TrailStep> Steps { get; } = [];

        public Task AppendAsync(Guid claimId, TrailStep step, string actor, string summary, object? payload, CancellationToken ct)
        {
            claimId.ShouldBe(ClaimId);
            summary.ShouldNotBeNullOrWhiteSpace();
            Steps.Add(step);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTraces : ITraceWriter
    {
        public List<string> Spans { get; } = [];

        public IDisposable? StartSpan(string name, IReadOnlyDictionary<string, object?>? tags = null)
        {
            Spans.Add(name);
            return null;
        }
    }
}
