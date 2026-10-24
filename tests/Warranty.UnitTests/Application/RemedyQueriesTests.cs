using NSubstitute;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Domain.Review;
using Warranty.Guardrails.Remedies;
using Warranty.UnitTests.Domain;

namespace Warranty.UnitTests.Application;

/// <summary>
/// The remedy read models (T229): the preview uses the same evaluator and version-selection rule as the remedy
/// set at approval (research R2, R13), and the remedy view carries its policy basis, history and ETag.
/// </summary>
public sealed class RemedyQueriesTests
{
    private static readonly Guid Tenant = Guid.Parse("0199b000-0000-7000-8000-000000000229");
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-0000000022d7");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000022e7");
    private static readonly Guid PolicyId = Guid.Parse("0199b000-0000-7000-8000-0000000022b1");
    private static readonly Guid VersionId = Guid.Parse("0199b000-0000-7000-8000-0000000022b2");
    private static readonly DateTimeOffset At = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private const string ReviewerSub = "reviewer-sub-t229";

    private readonly IRemedyRepository _remedies = Substitute.For<IRemedyRepository>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly IReviewRepository _reviews = Substitute.For<IReviewRepository>();
    private readonly Product _product = Product.Create(ProductId, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", 349m, "USD");

    public RemedyQueriesTests()
    {
        _policies.GetPolicyAsync(PolicyId, Arg.Any<CancellationToken>()).Returns(WarrantyPolicy.Create(PolicyId, Tenant, "AUR-WP", "Aurora Warranty"));
        _reviews.GetForClaimAsync(default, default).ReturnsForAnyArgs([]);
        _remedies.GetDecisionsAsync(default, default).ReturnsForAnyArgs([]);
        UseVersions(Version(VersionId, RemedyRulesContract.Aurora));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Preview ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_single_remedy_is_previewed_as_set_automatically()
    {
        var preview = await Queries().PreviewAsync(NewClaim(), _product, RemedyScenario.CoveredDefect, Ct);

        preview.ShouldBe(new RemedyPreview(RemedyScenario.CoveredDefect, preview.AllowedRemedies, RemedyType.Repair, false, "coveredDefect"));
        preview.AllowedRemedies.ShouldBe([RemedyType.Repair]);
    }

    [Fact]
    public async Task A_choice_needs_a_reviewer_and_has_no_recommendation()
    {
        UseVersions(Version(VersionId, RemedyRulesContract.Aurora with
        {
            CoveredDefect = new RemedyChoice(null, [RemedyType.Repair, RemedyType.Replacement]),
        }));

        var preview = await Queries().PreviewAsync(NewClaim(), _product, RemedyScenario.CoveredDefect, Ct);

        preview.AllowedRemedies.ShouldBe([RemedyType.Repair, RemedyType.Replacement]);
        preview.RecommendedRemedy.ShouldBeNull();
        preview.NeedsChoice.ShouldBeTrue();
    }

    [Fact]
    public async Task A_version_without_remedy_rules_previews_no_remedy_and_needs_a_reviewer()
    {
        UseVersions(Version(VersionId, rules: null));

        var preview = await Queries().PreviewAsync(NewClaim(), _product, RemedyScenario.CoveredDefect, Ct);

        preview.AllowedRemedies.ShouldBeEmpty();
        preview.RecommendedRemedy.ShouldBeNull();
        preview.NeedsChoice.ShouldBeTrue();
        preview.TableRow.ShouldBe("coveredDefect");
    }

    [Fact]
    public async Task Without_exactly_one_applicable_version_there_are_no_rules()
    {
        UseVersions(Version(VersionId, RemedyRulesContract.Aurora), Version(Guid.CreateVersion7(), RemedyRulesContract.Aurora));

        var ambiguous = await Queries().PreviewAsync(NewClaim(), _product, RemedyScenario.CoveredDefect, Ct);
        var noRegion = await Queries().PreviewAsync(NewClaim(region: null), _product, RemedyScenario.CoveredDefect, Ct);

        ambiguous.AllowedRemedies.ShouldBeEmpty();
        ambiguous.NeedsChoice.ShouldBeTrue();
        noRegion.AllowedRemedies.ShouldBeEmpty();
        noRegion.NeedsChoice.ShouldBeTrue();
    }

    [Fact]
    public void The_preview_is_the_evaluator_output()
    {
        var evaluation = new RemedyPolicyEvaluator().Evaluate(RemedyRulesContract.Borealis, RemedyScenario.ProtectionPlanDamage);

        RemedyQueries.ToPreview(evaluation).ShouldBe(new RemedyPreview(
            RemedyScenario.ProtectionPlanDamage, evaluation.AllowedRemedies, evaluation.SingleRemedy, evaluation.AutomaticRemedy is null,
            "protectionPlan"));
    }

    // ── Protection plan (T262, research R9, R13) ───────────────────────────────────────────────

    [Fact]
    public async Task With_a_declared_plan_that_the_version_honours_the_plan_preview_reads_row_protectionPlan()
    {
        UseVersions(Version(VersionId, RemedyRulesContract.Borealis));

        var preview = await Queries().PreviewWithProtectionPlanAsync(NewClaim(hasProtectionPlan: true), _product, Ct);

        preview.ShouldNotBeNull().Scenario.ShouldBe(RemedyScenario.ProtectionPlanDamage);
        preview.TableRow.ShouldBe("protectionPlan");
        preview.AllowedRemedies.ShouldBe([RemedyType.Repair]);
    }

    [Fact]
    public async Task Without_a_declared_plan_or_a_version_that_honours_it_there_is_no_plan_preview()
    {
        (await Queries().PreviewWithProtectionPlanAsync(NewClaim(hasProtectionPlan: true), _product, Ct)).ShouldBeNull("Aurora's plan does not lift the exclusion");

        UseVersions(Version(VersionId, RemedyRulesContract.Borealis));
        (await Queries().PreviewWithProtectionPlanAsync(NewClaim(), _product, Ct)).ShouldBeNull("the claim declares no plan");
    }

    [Theory]
    [InlineData(true, true, RemedyScenario.ProtectionPlanDamage)]
    [InlineData(true, false, RemedyScenario.CoveredDefect)]
    [InlineData(false, true, RemedyScenario.CoveredDefect)]
    public void The_initial_scenario_is_ProtectionPlanDamage_only_for_a_confirmed_plan_the_version_honours(
        bool confirmed, bool honoured, RemedyScenario expected)
    {
        var version = Version(VersionId, honoured ? RemedyRulesContract.Borealis : RemedyRulesContract.Aurora);

        RemedyQueries.InitialScenario(confirmed, version).ShouldBe(expected);
    }

    // ── Remedy view ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_claim_without_a_remedy_has_no_remedy_view()
        => (await Queries().GetRemedyAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();

    [Fact]
    public async Task The_remedy_view_has_the_policy_basis_the_history_with_reviewer_names_and_the_etag()
    {
        var claimId = Guid.CreateVersion7();
        var allowed = new[] { RemedyType.Repair, RemedyType.Replacement };
        var remedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claimId, VersionId, RemedyScenario.CoveredDefect, allowed, RemedyPendingReason.PolicyChoice, At);
        remedy.Decide(RemedyType.Replacement, At.AddMinutes(5));
        _remedies.GetAsync(claimId, Arg.Any<CancellationToken>()).Returns(remedy);
        _remedies.GetRowVersionAsync(claimId, Arg.Any<CancellationToken>()).Returns(4711u);
        _remedies.GetDecisionsAsync(claimId, Arg.Any<CancellationToken>()).Returns(
        [
            RemedyDecision.Decided(
                Guid.CreateVersion7(), Tenant, claimId, 2, RemedyScenario.CoveredDefect, VersionId, "coveredDefect", allowed,
                RemedyType.Replacement, RemedyDecidedBy.Reviewer, ReviewerSub, "The customer needs a working unit quickly.", 0, null,
                At.AddMinutes(5)),
            RemedyDecision.Evaluated(
                Guid.CreateVersion7(), Tenant, claimId, 1, RemedyScenario.CoveredDefect, VersionId, "coveredDefect", allowed, 0, null, At),
        ]);
        _reviews.GetForClaimAsync(claimId, Arg.Any<CancellationToken>()).Returns(
        [
            ReviewDecision.Create(
                Guid.CreateVersion7(), Tenant, claimId, Guid.CreateVersion7(), ReviewerSub, "Aurora Reviewer", ReviewDecisionKind.Approve,
                null, "Your tablet is covered.", null, null, At),
        ]);

        var view = (await Queries().GetRemedyAsync(claimId, Ct)).ShouldNotBeNull();

        view.State.ShouldBe(RemedyState.InProgress);
        view.Remedy.ShouldBe(RemedyType.Replacement);
        view.Scenario.ShouldBe(RemedyScenario.CoveredDefect);
        view.AllowedRemedies.ShouldBe(allowed);
        view.RecommendedRemedy.ShouldBeNull("the row gives a choice");
        view.PendingReason.ShouldBeNull();
        view.PolicyVersion.ShouldBe(new RemedyPolicyVersionView("AUR-WP", 2));
        view.TableRow.ShouldBe("coveredDefect");
        view.Completion.ShouldBeNull();
        view.ETag.ShouldBe("\"4711\"");

        view.History.Select(h => (h.Seq, h.Kind)).ShouldBe([(1, RemedyDecisionKind.Evaluated), (2, RemedyDecisionKind.Decided)]);
        view.History[0].Actor.ShouldBe(RemedyDecision.SystemActor);
        view.History[0].Remedy.ShouldBeNull();
        var decided = view.History[1];
        (decided.Remedy, decided.DecidedBy, decided.Actor, decided.Reason).ShouldBe(
            (RemedyType.Replacement, RemedyDecidedBy.Reviewer, "Aurora Reviewer", "The customer needs a working unit quickly."));
    }

    [Fact]
    public async Task A_single_allowed_remedy_is_the_recommendation_and_a_completion_is_shown()
    {
        var claimId = Guid.CreateVersion7();
        var remedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claimId, VersionId, RemedyScenario.RepairFailed, [RemedyType.Refund],
            RemedyPendingReason.RepairFailedLimit, At);
        remedy.Decide(RemedyType.Refund, At);
        var completion = RemedyCompletion.Record(Guid.CreateVersion7(), remedy, new DateOnly(2026, 9, 1), "RF-0001", null, "staff-sub", At);
        remedy.Complete(RemedyType.Refund, At);
        _remedies.GetAsync(claimId, Arg.Any<CancellationToken>()).Returns(remedy);
        _remedies.GetRowVersionAsync(claimId, Arg.Any<CancellationToken>()).Returns(7u);
        _remedies.GetCompletionAsync(claimId, Arg.Any<CancellationToken>()).Returns(completion);

        var view = (await Queries().GetRemedyAsync(claimId, Ct)).ShouldNotBeNull();

        view.State.ShouldBe(RemedyState.Completed);
        view.RecommendedRemedy.ShouldBe(RemedyType.Refund);
        view.TableRow.ShouldBe("repairFailed", "without decisions the row follows the scenario");
        view.Completion.ShouldBe(new RemedyCompletionView(new DateOnly(2026, 9, 1), "RF-0001", "staff-sub"));
    }

    private RemedyQueries Queries() => new(_remedies, _policies, _reviews, new RemedyPolicyEvaluator());

    private void UseVersions(params PolicyVersion[] versions)
    {
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns(versions);
        foreach (var version in versions)
        {
            _policies.GetVersionAsync(version.Id, Arg.Any<CancellationToken>()).Returns(version);
        }
    }

    private static PolicyVersion Version(Guid id, RemedyRules? rules)
    {
        var terms = new CoverageTerms(
            new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 }, new Dictionary<string, int>(),
            AccidentalDamageTerms.NotCovered, [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage]);
        return PolicyVersion.Create(
            id, Tenant, PolicyId, 2, new DateOnly(2025, 1, 1), null, [Region.NA, Region.EU], [], terms,
            "tenant-aurora/policies/AUR-WP-v2.md", "checksum", rules);
    }

    private static Claim NewClaim(Region? region = Region.NA, bool hasProtectionPlan = false) => Claim.Submit(
        Guid.CreateVersion7(), Tenant, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, CustomerId,
        "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, "SN-1001", new DateOnly(2026, 1, 15), "Aurora Store",
        349m, region, "The tablet does not power on after charging overnight.", At, hasProtectionPlan: hasProtectionPlan);
}
