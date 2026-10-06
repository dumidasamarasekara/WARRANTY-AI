namespace Warranty.Evaluation.Metrics;

/// <summary>A confidence band [<see cref="Min"/>, <see cref="Max"/>); the last band also includes 100.</summary>
public sealed record ConfidenceBand(int Min, int Max)
{
    public string Label => Max >= 100 ? $"{Min}-100" : $"{Min}-{Max - 1}";

    public bool Contains(int confidence) => confidence >= Min && (confidence < Max || (Max >= 100 && confidence == 100));
}

public sealed record BandResult(string Band, int Count, int Correct, double? Accuracy, double? MeanConfidence);

public sealed record CalibrationResult(double? BrierScore, int Count, IReadOnlyList<BandResult> Bands);

/// <summary>
/// Confidence calibration of the recommendation (research R19): the Brier score
/// <c>mean((confidence/100 − correct)²)</c> over valid recommendations of cases that expect one (lower is
/// better; 0.25 is what a constant 50% achieves), and accuracy by confidence band, which should rise with
/// the band. The PoC uses the model-reported confidence as-is (plan: PoC simplifications).
/// </summary>
public static class Calibration
{
    public static readonly IReadOnlyList<ConfidenceBand> DefaultBands =
        [new(0, 50), new(50, 70), new(70, 85), new(85, 95), new(95, 100)];

    public static CalibrationResult Compute(IEnumerable<ScoredCase> cases, IReadOnlyList<ConfidenceBand>? bands = null)
    {
        ArgumentNullException.ThrowIfNull(cases);
        bands ??= DefaultBands;
        var points = cases
            .Where(c => c.Expected.Recommendation is not null && c.Observation.RecommendationValid && c.Observation.Confidence is not null)
            .Select(c => (Confidence: c.Observation.Confidence!.Value,
                Correct: string.Equals(c.Observation.Recommendation, c.Expected.Recommendation, StringComparison.Ordinal)))
            .ToList();

        return new CalibrationResult(
            BrierScore(points),
            points.Count,
            bands.Select(band =>
            {
                var inBand = points.Where(p => band.Contains(p.Confidence)).ToList();
                return new BandResult(
                    band.Label,
                    inBand.Count,
                    inBand.Count(p => p.Correct),
                    inBand.Count == 0 ? null : (double)inBand.Count(p => p.Correct) / inBand.Count,
                    inBand.Count == 0 ? null : inBand.Average(p => (double)p.Confidence));
            }).ToList());
    }

    /// <summary>Mean squared difference between the stated probability and the 0/1 outcome; null for no points.</summary>
    public static double? BrierScore(IReadOnlyCollection<(int Confidence, bool Correct)> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Count == 0
            ? null
            : points.Average(p => Math.Pow((p.Confidence / 100d) - (p.Correct ? 1d : 0d), 2));
    }
}
