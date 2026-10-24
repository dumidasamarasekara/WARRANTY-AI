using System.Text.Json;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;
using Warranty.UnitTests.Domain;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// Failed and uneconomical repairs (002 T245; FR-029 – FR-032, data-model.md "Repair" and <c>repair_attempts</c>): only
/// an attempt that ends as <see cref="AttemptResult.Failed"/> is counted; an estimate strictly above the economic limit
/// makes the repair not possible without counting a failure; after a failure below the limit a new attempt starts at the
/// centre or, when the product was returned to the customer, after a new hand-over; and the re-evaluation with the
/// count and the estimate gives each company's own result (quickstart R4, R8 – R10). The domain transitions are
/// implemented in T247.
/// </summary>
public sealed class RepairFailureTests
{
    private const string Actor = "agent-sub-t245";
    private const string FirstFailure = "The display flickers again after the panel was replaced.";
    private const string SecondFailure = "The new panel shows the same fault after one day.";

    /// <summary>Claim value of BOR-SLATE11 in quickstart R10; Borealis's 60 % limit is 288.00.</summary>
    private const decimal SlateClaimValue = 480m;

    private const decimal EconomicLimit = 288m;

    private static readonly Guid Tenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid ClaimId = Guid.Parse("0199b000-0000-7000-8000-000000000245");
    private static readonly Guid Centre = Guid.Parse("22222222-0003-7000-8000-000000000003");
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly HandedOverOn = new(2026, 9, 2);

    private static readonly RemedyPolicyEvaluator Evaluator = new();

    // ── Failure counting (FR-029) ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_failure_ends_the_open_attempt_as_failed_counts_it_and_makes_the_repair_not_possible()
    {
        var repair = AtRepairCentre();
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();

        var recorded = repair.RecordFailure(FirstFailure, Report(3));

        (repair.Status, repair.NotPossibleReason, repair.FailedAttempts).ShouldBe(
            (RepairStatus.RepairNotPossible, (RepairNotPossibleReason?)RepairNotPossibleReason.Failed, 1));
        (attempt.Result, attempt.FailureReason, attempt.EndedAt).ShouldBe(
            (AttemptResult.Failed, FirstFailure, (DateTimeOffset?)Report(3).RecordedAt));
        attempt.IsOpen.ShouldBeFalse();
        (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Actor).ShouldBe(
            (RepairEventType.RepairFailed, (RepairStatus?)RepairStatus.AtRepairCentre, (RepairStatus?)RepairStatus.RepairNotPossible, Actor));
        Payload(recorded).GetProperty("attemptId").GetGuid().ShouldBe(attempt.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_failure_needs_a_reason(string? reason)
    {
        var repair = AtRepairCentre();
        var events = repair.Events.Count;

        Should.Throw<ArgumentException>(() => repair.RecordFailure(reason!, Report(3)));

        (repair.Status, repair.FailedAttempts, repair.Events.Count).ShouldBe((RepairStatus.AtRepairCentre, 0, events));
        repair.CurrentAttempt.ShouldNotBeNull().IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void Each_failed_attempt_is_counted_once()
    {
        var repair = AtRepairCentre();

        repair.RecordFailure(FirstFailure, Report(3));
        repair.StartNewAttempt(ProductLocation.AtCentre, Report(4));
        repair.RecordFailure(SecondFailure, Report(6));

        repair.FailedAttempts.ShouldBe(2);
        repair.Attempts.Select(a => (a.Seq, a.Result, a.FailureReason)).ShouldBe(
            [(1, AttemptResult.Failed, FirstFailure), (2, AttemptResult.Failed, SecondFailure)]);
        repair.NotPossibleReason.ShouldBe(RepairNotPossibleReason.Failed);
    }

    [Fact]
    public void Estimates_and_failures_need_the_product_at_the_centre()
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, Centre, CreatedAt);

        Should.Throw<InvalidOperationException>(() => repair.RecordFailure(FirstFailure, Report(1))).Message
            .ShouldContain(nameof(RepairStatus.AwaitingHandover));
        Should.Throw<InvalidOperationException>(() => repair.RecordEstimate(100m, EconomicLimit, Report(1))).Message
            .ShouldContain(nameof(RepairStatus.AwaitingHandover));

        (repair.Status, repair.FailedAttempts, repair.Events.Count).ShouldBe((RepairStatus.AwaitingHandover, 0, 1));
    }

    // ── Not economical is never a failure (FR-004, FR-029, FR-032) ─────────────────────────────

    [Theory]
    [InlineData(288.00, 288.00)]
    [InlineData(0.00, 288.00)]
    [InlineData(10000.00, null)]
    public void An_estimate_at_or_below_the_limit_or_without_a_limit_keeps_the_repair_at_the_centre(double estimate, double? limit)
    {
        var repair = AtRepairCentre();
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();

        var recorded = repair.RecordEstimate((decimal)estimate, (decimal?)limit, Report(3));

        (repair.Status, repair.NotPossibleReason, repair.FailedAttempts).ShouldBe(
            (RepairStatus.AtRepairCentre, (RepairNotPossibleReason?)null, 0));
        (attempt.Result, attempt.EstimateAmount).ShouldBe((AttemptResult.Open, (decimal?)(decimal)estimate));
        (recorded.Type, recorded.FromStatus, recorded.ToStatus).ShouldBe(
            (RepairEventType.EstimateReported, (RepairStatus?)RepairStatus.AtRepairCentre, (RepairStatus?)RepairStatus.AtRepairCentre));
        repair.AllowedEvents().ShouldContain(RepairEventType.MarkedReady);
    }

    [Fact]
    public void An_estimate_one_cent_above_the_limit_makes_the_repair_not_economical_without_counting_a_failure()
    {
        var repair = AtRepairCentre();
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();

        var recorded = repair.RecordEstimate(288.01m, EconomicLimit, Report(3));

        (repair.Status, repair.NotPossibleReason, repair.FailedAttempts).ShouldBe(
            (RepairStatus.RepairNotPossible, (RepairNotPossibleReason?)RepairNotPossibleReason.NotEconomical, 0));
        (attempt.Result, attempt.EstimateAmount, attempt.EndedAt).ShouldBe(
            (AttemptResult.NotEconomical, (decimal?)288.01m, (DateTimeOffset?)Report(3).RecordedAt));
        attempt.FailureReason.ShouldNotBeNullOrWhiteSpace();
        (recorded.Type, recorded.ToStatus).ShouldBe((RepairEventType.EstimateReported, (RepairStatus?)RepairStatus.RepairNotPossible));
        var payload = Payload(recorded);
        payload.GetProperty("estimateAmount").GetDecimal().ShouldBe(288.01m);
        payload.GetProperty("economicLimitAmount").GetDecimal().ShouldBe(EconomicLimit);
    }

    [Fact]
    public void An_uneconomical_estimate_after_a_failure_leaves_the_failure_count_unchanged()
    {
        var repair = AtRepairCentre();
        repair.RecordFailure(FirstFailure, Report(3));
        repair.StartNewAttempt(ProductLocation.AtCentre, Report(4));

        repair.RecordEstimate(400m, EconomicLimit, Report(5));

        repair.FailedAttempts.ShouldBe(1);
        repair.Attempts.Select(a => a.Result).ShouldBe([AttemptResult.Failed, AttemptResult.NotEconomical]);
    }

    [Fact]
    public void A_negative_estimate_is_refused()
    {
        var repair = AtRepairCentre();

        Should.Throw<ArgumentException>(() => repair.RecordEstimate(-0.01m, EconomicLimit, Report(3)));

        repair.CurrentAttempt.ShouldNotBeNull().EstimateAmount.ShouldBeNull();
        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
    }

    [Fact]
    public void After_an_uneconomical_estimate_no_new_attempt_is_allowed()
    {
        var repair = AtRepairCentre();
        repair.RecordEstimate(288.01m, EconomicLimit, Report(3));
        var events = repair.Events.Count;

        repair.AllowedEvents().ShouldNotContain(RepairEventType.NewAttemptStarted);
        Should.Throw<InvalidOperationException>(() => repair.StartNewAttempt(ProductLocation.AtCentre, Report(4))).Message
            .ShouldContain(nameof(RepairStatus.RepairNotPossible));
        (repair.Status, repair.Attempts.Count, repair.Events.Count).ShouldBe((RepairStatus.RepairNotPossible, 1, events));
    }

    // ── A new attempt after a failure (FR-030) ─────────────────────────────────────────────────

    [Fact]
    public void A_new_attempt_with_the_product_still_at_the_centre_opens_the_next_attempt_there()
    {
        var repair = AtRepairCentre();
        repair.RecordFailure(FirstFailure, Report(3));
        repair.AllowedEvents().ShouldContain(RepairEventType.NewAttemptStarted);

        var recorded = repair.StartNewAttempt(ProductLocation.AtCentre, Report(4));

        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        repair.NotPossibleReason.ShouldBeNull();
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();
        (attempt.Seq, attempt.ServiceCenterId, attempt.Result).ShouldBe((2, Centre, AttemptResult.Open));
        repair.Attempts[0].Result.ShouldBe(AttemptResult.Failed);
        repair.FailedAttempts.ShouldBe(1);
        (recorded.Type, recorded.FromStatus, recorded.ToStatus).ShouldBe(
            (RepairEventType.NewAttemptStarted, (RepairStatus?)RepairStatus.RepairNotPossible, (RepairStatus?)RepairStatus.AtRepairCentre));
        Payload(recorded).GetProperty("productLocation").GetString().ShouldBe(nameof(ProductLocation.AtCentre));
    }

    [Fact]
    public void A_new_attempt_after_the_product_was_returned_waits_for_a_new_hand_over_which_opens_the_next_attempt()
    {
        var repair = AtRepairCentre();
        repair.RecordFailure(FirstFailure, Report(3));

        repair.StartNewAttempt(ProductLocation.ReturnedToCustomer, Report(4));

        repair.Status.ShouldBe(RepairStatus.AwaitingHandover);
        repair.NotPossibleReason.ShouldBeNull();
        repair.Attempts.ShouldHaveSingleItem().Result.ShouldBe(AttemptResult.Failed);
        repair.AllowedEvents().ShouldContain(RepairEventType.HandedOver);

        repair.RecordHandover(HandedOverOn.AddDays(6), "JOB-2002", Report(6));

        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();
        (attempt.Seq, attempt.ServiceCenterId, attempt.Result).ShouldBe((2, Centre, AttemptResult.Open));
        repair.FailedAttempts.ShouldBe(1);
    }

    [Fact]
    public void A_new_attempt_needs_a_failed_repair()
    {
        var repair = AtRepairCentre();

        Should.Throw<InvalidOperationException>(() => repair.StartNewAttempt(ProductLocation.AtCentre, Report(3))).Message
            .ShouldContain(nameof(RepairStatus.AtRepairCentre));
        repair.Attempts.ShouldHaveSingleItem();
    }

    // ── Re-evaluation with each company's rules (R4, R8 – R10) ─────────────────────────────────

    [Theory]
    [InlineData("Aurora", RemedyType.Replacement)]
    [InlineData("Borealis", RemedyType.Refund)]
    public void Below_the_attempt_limit_the_repair_continues_and_at_the_limit_a_reviewer_confirms_the_company_remedy(
        string company, RemedyType remedy)
    {
        var rules = RulesOf(company);

        var first = Evaluator.Evaluate(rules, RemedyScenario.RepairFailed, failedAttempts: 1);
        (first.ContinueRepair, first.NeedsReviewer, first.PendingReason).ShouldBe((true, false, (RemedyPendingReason?)null));
        first.AllowedRemedies.ShouldBeEmpty();

        foreach (var failed in new[] { 2, 3 })
        {
            var atLimit = Evaluator.Evaluate(rules, RemedyScenario.RepairFailed, failedAttempts: failed);
            (atLimit.ContinueRepair, atLimit.NeedsReviewer, atLimit.PendingReason, atLimit.TableRow).ShouldBe(
                (false, true, (RemedyPendingReason?)RemedyPendingReason.RepairFailedLimit, "repairFailed"));
            atLimit.AllowedRemedies.ShouldBe([remedy]);
            atLimit.SingleRemedy.ShouldBe(remedy, "the single remedy is the recommendation");
            atLimit.AutomaticRemedy.ShouldBeNull("FR-031: a reviewer confirms even a single remedy after a failure");
        }
    }

    [Fact]
    public void The_same_facts_get_each_company_s_own_remedy_at_the_limit()
    {
        var aurora = Evaluator.Evaluate(RemedyRulesContract.Aurora, RemedyScenario.RepairFailed, failedAttempts: 2);
        var borealis = Evaluator.Evaluate(RemedyRulesContract.Borealis, RemedyScenario.RepairFailed, failedAttempts: 2);

        (aurora.SingleRemedy, borealis.SingleRemedy).ShouldBe(((RemedyType?)RemedyType.Replacement, (RemedyType?)RemedyType.Refund));
    }

    [Theory]
    [InlineData(288.00, true)]
    [InlineData(288.01, false)]
    public void Borealis_continues_the_repair_up_to_60_percent_of_the_claim_value_and_then_offers_a_choice(double estimate, bool continues)
    {
        var evaluation = Evaluator.Evaluate(
            RemedyRulesContract.Borealis, RemedyScenario.RepairNotEconomical, estimate: (decimal)estimate, claimValue: SlateClaimValue);

        evaluation.EconomicLimitAmount.ShouldBe(EconomicLimit);
        evaluation.ContinueRepair.ShouldBe(continues);
        if (!continues)
        {
            (evaluation.NeedsReviewer, evaluation.PendingReason, evaluation.SingleRemedy).ShouldBe(
                (true, (RemedyPendingReason?)RemedyPendingReason.RepairNotEconomical, (RemedyType?)null));
            evaluation.AllowedRemedies.ShouldBe([RemedyType.Refund, RemedyType.Replacement], ignoreOrder: true);
        }
    }

    [Fact]
    public void Aurora_has_no_economic_limit_so_no_estimate_ends_the_repair()
    {
        var evaluation = Evaluator.Evaluate(
            RemedyRulesContract.Aurora, RemedyScenario.RepairNotEconomical, estimate: 10_000m, claimValue: 450m);

        (evaluation.ContinueRepair, evaluation.NeedsReviewer, evaluation.EconomicLimitAmount).ShouldBe((true, false, (decimal?)null));
    }

    [Theory]
    [InlineData("Aurora", RemedyType.Replacement)]
    [InlineData("Borealis", RemedyType.Refund)]
    public void The_repair_s_own_failure_count_drives_the_re_evaluation(string company, RemedyType remedy)
    {
        var rules = RulesOf(company);
        var repair = AtRepairCentre();

        repair.RecordFailure(FirstFailure, Report(3));
        Evaluator.Evaluate(rules, RemedyScenario.RepairFailed, repair.FailedAttempts).ContinueRepair.ShouldBeTrue();

        repair.StartNewAttempt(ProductLocation.AtCentre, Report(4));
        repair.RecordFailure(SecondFailure, Report(6));
        var atLimit = Evaluator.Evaluate(rules, RemedyScenario.RepairFailed, repair.FailedAttempts);

        (atLimit.NeedsReviewer, atLimit.SingleRemedy).ShouldBe((true, (RemedyType?)remedy));
    }

    [Fact]
    public void The_evaluator_s_economic_limit_decides_the_repair_s_estimate()
    {
        var limit = Evaluator.Evaluate(
            RemedyRulesContract.Borealis, RemedyScenario.RepairNotEconomical, estimate: 288.01m, claimValue: SlateClaimValue)
            .EconomicLimitAmount;
        var repair = AtRepairCentre();

        repair.RecordEstimate(288.01m, limit, Report(3));

        repair.NotPossibleReason.ShouldBe(RepairNotPossibleReason.NotEconomical);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static RemedyRules RulesOf(string company)
        => company == "Aurora" ? RemedyRulesContract.Aurora : RemedyRulesContract.Borealis;

    private static JsonElement Payload(RepairEvent repairEvent) => JsonDocument.Parse(repairEvent.PayloadJson).RootElement.Clone();

    /// <summary>A report by <see cref="Actor"/> by phone, <paramref name="day"/> days after the hand-over.</summary>
    private static RepairReport Report(int day)
    {
        var at = new DateTimeOffset(HandedOverOn.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new RepairReport(at, ReportSource.Phone, Actor, null, at.AddMinutes(5));
    }

    /// <summary>A repair handed over to <see cref="Centre"/> with attempt 1 open.</summary>
    private static Repair AtRepairCentre()
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, Centre, CreatedAt);
        repair.RecordHandover(HandedOverOn, "JOB-1001", Report(0));
        return repair;
    }
}
