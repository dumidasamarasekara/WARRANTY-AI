using Warranty.Evaluation.Metrics;
using static Warranty.UnitTests.Evaluation.EvaluationCases;

namespace Warranty.UnitTests.Evaluation;

/// <summary>Recommendation and disposition accuracy, and escalation recall.</summary>
public sealed class OutcomeMetricsTests
{
    [Fact]
    public void Recommendation_accuracy_counts_only_valid_matching_recommendations_per_tenant()
    {
        var cases = new[]
        {
            Scored(Expect("APPROVE"), Observe("G-AUR-01", "aurora", "APPROVE")),
            Scored(Expect("REJECT"), Observe("G-AUR-02", "aurora", "REJECT", valid: false)),
            Scored(Expect("HUMAN_REVIEW", "HumanReview"), Observe("G-BOR-01", "borealis", null, valid: false, disposition: "HumanReview")),
            Scored(Expect(null, "RequestInformation"), Observe("G-BOR-02", "borealis", null, valid: false, disposition: "RequestInformation")),
        };

        var result = OutcomeAccuracy.Recommendation(cases);

        result.Overall.ShouldBe(new Ratio(1, 3));
        result.ByTenant["aurora"].ShouldBe(new Ratio(1, 2));
        result.ByTenant["borealis"].ShouldBe(new Ratio(0, 1));
        result.ByExpected["REJECT"].ShouldBe(new Ratio(0, 1));
        result.MissedCaseIds.ShouldBe(["G-AUR-02", "G-BOR-01"]);
    }

    [Fact]
    public void Disposition_accuracy_scores_every_case_including_intake_short_circuits()
    {
        var cases = new[]
        {
            Scored(Expect("APPROVE", "AutoApprove"), Observe("G-AUR-01", disposition: "HumanReview")),
            Scored(Expect(null, "RequestInformation"), Observe("G-AUR-02", recommendation: null, disposition: "RequestInformation")),
        };

        var result = OutcomeAccuracy.Disposition(cases);

        result.Overall.ShouldBe(new Ratio(1, 2));
        result.MissedCaseIds.ShouldBe(["G-AUR-01"]);
        result.Overall.Value.ShouldBe(0.5);
    }

    [Fact]
    public void Escalation_recall_lists_auto_finalized_should_escalate_cases_and_missing_reasons()
    {
        var cases = new[]
        {
            Scored(Expect("HUMAN_REVIEW", "HumanReview", ["RISK_HIGH"]), Observe("G-AUR-01", disposition: "HumanReview", reasons: ["RISK_HIGH", "AI_RECOMMENDS_REVIEW"])),
            Scored(Expect("APPROVE", "HumanReview", ["VALUE_ABOVE_LIMIT"]), Observe("G-AUR-02", disposition: "AutoApprove")),
            Scored(Expect("REQUEST_MORE_INFORMATION", "HumanReview", ["INFO_INCOMPLETE_AFTER_2_REQUESTS"]), Observe("G-AUR-03", disposition: "RequestInformation")),
            Scored(Expect("APPROVE", "AutoApprove"), Observe("G-AUR-04", disposition: "HumanReview")),
        };

        var result = EscalationRecall.Compute(cases);

        result.Recall.ShouldBe(new Ratio(1, 3));
        result.AutoFinalizedCaseIds.ShouldBe(["G-AUR-02"]);
        result.NotEscalatedCaseIds.ShouldBe(["G-AUR-02", "G-AUR-03"]);
        result.ReasonRecall.ShouldBe(new Ratio(1, 3));
        result.MissingReasons.ShouldBe(["G-AUR-02: VALUE_ABOVE_LIMIT", "G-AUR-03: INFO_INCOMPLETE_AFTER_2_REQUESTS"]);
    }

    [Fact]
    public void Only_evaluated_cases_are_scored_and_unscored_targets_are_not_measured()
    {
        var cases = new[]
        {
            Scored(Expect("APPROVE"), Observe("G-AUR-01", outcome: CaseOutcomeKind.NotRecorded, disposition: "HumanReview")),
            Scored(Expect("APPROVE"), Observe("G-AUR-02", outcome: CaseOutcomeKind.Failed, disposition: null)),
        };

        var metrics = EvaluationMetrics.Compute(cases);

        metrics.CasesEvaluated.ShouldBe(0);
        metrics.Disposition.Overall.Total.ShouldBe(0);
        metrics.Targets().ShouldAllBe(t => t.Passed == null);
    }

    [Fact]
    public void Targets_apply_the_85_percent_and_100_percent_thresholds()
    {
        var cases = Enumerable.Range(1, 7)
            .Select(i => Scored(Expect("APPROVE"), Observe($"G-AUR-{i:00}", cited: ["EV-1"], issued: ["EV-1"])))
            .Append(Scored(Expect("REJECT", "AutoReject"), Observe("G-AUR-08", recommendation: "APPROVE", cited: ["EV-1"], issued: ["EV-1"])))
            .Append(Scored(Expect("HUMAN_REVIEW", "HumanReview"), Observe("G-AUR-09", recommendation: "HUMAN_REVIEW", disposition: "HumanReview")))
            .ToList();

        var targets = EvaluationMetrics.Compute(cases).Targets().ToDictionary(t => t.Metric);

        targets["Recommendation accuracy (aurora)"].Passed.ShouldBe(true, "8 of 9 is 88.9%");
        targets["Escalation recall"].Passed.ShouldBe(true);
        targets["Unsupported-reference rate"].Passed.ShouldBe(true);
    }
}
