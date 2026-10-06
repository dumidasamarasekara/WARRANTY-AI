using Warranty.Evaluation;
using Warranty.Evaluation.Metrics;
using static Warranty.UnitTests.Evaluation.EvaluationCases;

namespace Warranty.UnitTests.Evaluation;

/// <summary>Unsupported-reference rate, usage totals and the runner's command line.</summary>
public sealed class UnsupportedReferencesTests
{
    [Fact]
    public void References_come_from_the_ref_lists_and_the_reasoning_summary()
    {
        const string raw = """
            {
              "decision": "APPROVE",
              "evidenceRefs": [ { "ref": "EV-1", "observation": "invoice" }, { "ref": "EV-9", "observation": "photo" } ],
              "policyRefs": [ { "ref": "POL-2", "relevance": "SUPPORTS_COVERAGE" } ],
              "reasoningSummary": "EV-1 shows the date; POL-2 and GLB-1 apply; EV-1 again."
            }
            """;

        UnsupportedReferences.FromRecommendationJson(raw).ShouldBe(["EV-1", "EV-9", "POL-2", "GLB-1"]);
    }

    [Fact]
    public void Malformed_output_is_still_scanned_for_reference_ids()
    {
        UnsupportedReferences.FromRecommendationJson("""{ "decision": "APPROVE", "policyRefs": [ { "ref": "POL-7" """).ShouldBe(["POL-7"]);
        UnsupportedReferences.FromRecommendationJson(null).ShouldBeEmpty();
    }

    [Fact]
    public void The_rate_is_unsupported_distinct_references_over_all_cited_references()
    {
        var cases = new[]
        {
            Scored(Expect(), Observe("G-AUR-01", issued: ["EV-1", "EV-2", "POL-1"], cited: ["EV-1", "pol-1", "POL-1"])),
            Scored(Expect(), Observe("G-AUR-02", valid: false, issued: ["EV-1"], cited: ["EV-1", "EV-3", "POL-4"])),
            Scored(Expect(null, "RequestInformation"), Observe("G-AUR-03", recommendation: null, cited: [])),
        };

        var result = UnsupportedReferences.Compute(cases);

        result.Recommendations.ShouldBe(2);
        result.CitedReferences.ShouldBe(5);
        result.Unsupported.ShouldBe(2);
        result.Rate.ShouldBe(0.4);
        result.UnsupportedDetails.ShouldBe(["G-AUR-02: EV-3", "G-AUR-02: POL-4"]);
    }

    [Fact]
    public void No_citation_means_no_rate()
        => UnsupportedReferences.Compute([Scored(Expect(null, "RequestInformation"), Observe(recommendation: null))]).Rate.ShouldBeNull();

    [Fact]
    public void Usage_sums_tokens_cache_reads_and_cost_per_agent()
    {
        var a = Observe("G-AUR-01") with
        {
            Usage = new UsageTotals(2, 0, 1000, 100, 400, 50, 0.02m),
            UsageByAgent = new Dictionary<string, UsageTotals>
            {
                ["intake"] = new(1, 0, 400, 40, 0, 50, 0.005m),
                ["decision"] = new(1, 0, 600, 60, 400, 0, 0.015m),
            },
        };
        var b = Observe("G-AUR-02") with
        {
            Usage = new UsageTotals(1, 1, 500, 0, 0, 0, 0.01m),
            UsageByAgent = new Dictionary<string, UsageTotals> { ["intake"] = new(1, 1, 500, 0, 0, 0, 0.01m) },
        };

        var usage = UsageSummary.Compute([Scored(Expect(), a), Scored(Expect(), b)]);

        usage.Total.ShouldBe(new UsageTotals(3, 1, 1500, 100, 400, 50, 0.03m));
        usage.ByAgent["intake"].ShouldBe(new UsageTotals(2, 1, 900, 40, 0, 50, 0.015m));
        usage.CostPerCase.ShouldBe(0.015m);
        usage.CacheReadShare!.Value.ShouldBe(400d / 1950, tolerance: 1e-9);
    }

    [Fact]
    public void Recording_requires_live_mode_and_the_mode_is_required()
    {
        EvaluationOptions.Parse(["--mode", "live", "--record", "--tenants", "aurora, borealis"])
            .ShouldSatisfyAllConditions(
                o => o.Mode.ShouldBe(EvaluationMode.Live),
                o => o.Record.ShouldBeTrue(),
                o => o.Tenants.ShouldBe(["aurora", "borealis"]));
        Should.Throw<ArgumentException>(() => EvaluationOptions.Parse(["--mode", "replay", "--record"]));
        Should.Throw<ArgumentException>(() => EvaluationOptions.Parse(["--cases", "G-AUR-01"]));
        Should.Throw<ArgumentException>(() => EvaluationOptions.Parse(["--mode", "fast"]));
    }
}
