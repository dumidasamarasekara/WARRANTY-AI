using Warranty.Evaluation.Metrics;
using static Warranty.UnitTests.Evaluation.EvaluationCases;

namespace Warranty.UnitTests.Evaluation;

public sealed class RetrievalRecallTests
{
    private static readonly RetrievedClause[] Retrieved =
    [
        new("POL-1", "AUR-WP-2.1", 0.61),
        new("POL-2", "AUR-WP-3.2", 0.83),
        new("POL-3", "AUR-WP-1.1", 0.74),
        new("POL-4", "AUR-WP-3.2", 0.50),
        new("POL-5", "AUR-WP-4.0", 0.74),
    ];

    [Fact]
    public void Clause_keys_are_ranked_by_score_without_duplicates_ties_by_issue_order()
        => RetrievalRecall.Rank(Retrieved).ShouldBe(["AUR-WP-3.2", "AUR-WP-1.1", "AUR-WP-4.0", "AUR-WP-2.1"]);

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 0.5)]
    [InlineData(3, 0.5)]
    [InlineData(4, 1.0)]
    [InlineData(10, 1.0)]
    public void Recall_at_k_is_the_share_of_expected_keys_in_the_top_k(int k, double recall)
        => RetrievalRecall.RecallAt(["AUR-WP-1.1", "AUR-WP-2.1"], RetrievalRecall.Rank(Retrieved), k).ShouldBe(recall);

    [Fact]
    public void Mean_recall_covers_cases_that_expect_a_recommendation_and_list_clause_keys()
    {
        var cases = new[]
        {
            Scored(Expect(clauses: ["AUR-WP-1.1", "AUR-WP-2.1"]), Observe("G-AUR-01", retrieved: Retrieved)),
            Scored(Expect(clauses: ["AUR-WP-1.1"]), Observe("G-AUR-02", retrieved: [])),
            // Intake short-circuit: no recommendation, retrieval never runs by design.
            Scored(Expect(recommendation: null, disposition: "RequestInformation", clauses: ["AUR-WP-1.1"]), Observe("G-AUR-03", retrieved: [])),
            Scored(Expect(clauses: []), Observe("G-AUR-04", retrieved: Retrieved)),
        };

        var result = RetrievalRecall.Compute(cases, [2, 4]);

        result.CasesScored.ShouldBe(2);
        result.MeanRecallAtK[2].ShouldBe(0.25);
        result.MeanRecallAtK[4].ShouldBe(0.5);
        result.CasesWithoutRetrieval.ShouldBe(["G-AUR-02"]);
    }

    [Fact]
    public void No_scored_case_means_no_recall_value()
        => RetrievalRecall.Compute([]).MeanRecallAtK[5].ShouldBeNull();
}
