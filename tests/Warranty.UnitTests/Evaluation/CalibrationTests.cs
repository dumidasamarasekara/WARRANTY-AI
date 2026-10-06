using Warranty.Evaluation.Metrics;
using static Warranty.UnitTests.Evaluation.EvaluationCases;

namespace Warranty.UnitTests.Evaluation;

public sealed class CalibrationTests
{
    [Fact]
    public void Brier_score_is_the_mean_squared_gap_between_confidence_and_correctness()
    {
        // (0.9 − 1)² = 0.01, (0.8 − 0)² = 0.64, (0.5 − 1)² = 0.25 → mean 0.3
        Calibration.BrierScore([(90, true), (80, false), (50, true)])!.Value.ShouldBe(0.3, tolerance: 1e-9);
        Calibration.BrierScore([(100, true), (0, false)]).ShouldBe(0d);
        Calibration.BrierScore([]).ShouldBeNull();
    }

    [Fact]
    public void Only_valid_recommendations_with_a_confidence_of_cases_expecting_one_are_scored()
    {
        var cases = new[]
        {
            Scored(Expect("APPROVE"), Observe("G-AUR-01", recommendation: "APPROVE", confidence: 90)),
            Scored(Expect("APPROVE"), Observe("G-AUR-02", recommendation: "REJECT", confidence: 80)),
            Scored(Expect("APPROVE"), Observe("G-AUR-03", recommendation: "APPROVE", valid: false, confidence: 99)),
            Scored(Expect(null, "RequestInformation"), Observe("G-AUR-04", recommendation: "APPROVE", confidence: 99)),
            Scored(Expect("APPROVE"), Observe("G-AUR-05", recommendation: "APPROVE", confidence: null)),
        };

        var result = Calibration.Compute(cases);

        result.Count.ShouldBe(2);
        result.BrierScore!.Value.ShouldBe((0.01 + 0.64) / 2, tolerance: 1e-9);
    }

    [Fact]
    public void Accuracy_is_reported_per_confidence_band_with_100_in_the_top_band()
    {
        var cases = new[]
        {
            Scored(Expect("APPROVE"), Observe("G-AUR-01", recommendation: "APPROVE", confidence: 100)),
            Scored(Expect("APPROVE"), Observe("G-AUR-02", recommendation: "REJECT", confidence: 95)),
            Scored(Expect("REJECT"), Observe("G-AUR-03", recommendation: "REJECT", confidence: 72)),
            Scored(Expect("REJECT"), Observe("G-AUR-04", recommendation: "REJECT", confidence: 49)),
        };

        var bands = Calibration.Compute(cases).Bands.ToDictionary(b => b.Band);

        bands.Keys.ShouldBe(["0-49", "50-69", "70-84", "85-94", "95-100"]);
        bands["95-100"].ShouldBe(new BandResult("95-100", 2, 1, 0.5, 97.5));
        bands["70-84"].Accuracy.ShouldBe(1d);
        bands["0-49"].Count.ShouldBe(1);
        bands["50-69"].ShouldBe(new BandResult("50-69", 0, 0, null, null));
    }

    [Theory]
    [InlineData(84, false)]
    [InlineData(85, true)]
    [InlineData(94, true)]
    [InlineData(95, false)]
    public void Bands_include_their_minimum_and_exclude_their_maximum(int confidence, bool inBand)
        => new ConfidenceBand(85, 95).Contains(confidence).ShouldBe(inBand);
}
