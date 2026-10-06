namespace Warranty.Evaluation.Metrics;

/// <summary>A count of hits out of a total; <see cref="Value"/> is null when nothing was scored.</summary>
public sealed record Ratio(int Hits, int Total)
{
    public static readonly Ratio Empty = new(0, 0);

    public double? Value => Total == 0 ? null : (double)Hits / Total;

    public Ratio Add(bool hit) => new(Hits + (hit ? 1 : 0), Total + 1);

    public Ratio Add(Ratio other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Ratio(Hits + other.Hits, Total + other.Total);
    }

    /// <summary>Tallies <paramref name="items"/> by key, e.g. accuracy per tenant.</summary>
    public static IReadOnlyDictionary<string, Ratio> By<T>(IEnumerable<T> items, Func<T, string> key, Func<T, bool> hit)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new SortedDictionary<string, Ratio>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var k = key(item);
            result[k] = (result.TryGetValue(k, out var ratio) ? ratio : Empty).Add(hit(item));
        }

        return result;
    }

    public override string ToString() => Value is { } v ? $"{v:P1} ({Hits}/{Total})" : "n/a (0 scored)";
}

/// <summary>A golden case's labels together with what the system produced for it.</summary>
public sealed record ScoredCase(string CaseId, string Tenant, Golden.GoldenExpectation Expected, CaseObservation Observation);
