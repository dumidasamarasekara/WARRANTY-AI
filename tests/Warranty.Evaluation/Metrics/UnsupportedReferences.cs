using System.Text.Json;
using System.Text.RegularExpressions;

namespace Warranty.Evaluation.Metrics;

public sealed record UnsupportedReferenceResult(
    int Recommendations,
    int CitedReferences,
    int Unsupported,
    IReadOnlyList<string> UnsupportedDetails)
{
    /// <summary>Unsupported / cited (SC-006: must be 0); null when no reference was cited.</summary>
    public double? Rate => CitedReferences == 0 ? null : (double)Unsupported / CitedReferences;
}

/// <summary>
/// Unsupported-reference rate (research R19, SC-006): the references a recommendation's raw model output
/// uses — <c>evidenceRefs[].ref</c>, <c>policyRefs[].ref</c> and IDs mentioned in <c>reasoningSummary</c> —
/// that the harness did not issue for the run (an evidence ID that does not exist, a clause that was not
/// retrieved). Distinct references are counted per case; invalid recommendations are included, because
/// their raw output is exactly where an invented reference shows up.
/// </summary>
public static partial class UnsupportedReferences
{
    public static UnsupportedReferenceResult Compute(IEnumerable<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var withCitations = cases.Select(c => c.Observation).Where(o => o.CitedReferences.Count > 0 || o.Recommendation is not null).ToList();
        var cited = 0;
        var details = new List<string>();
        foreach (var observation in withCitations)
        {
            var issued = observation.IssuedReferences.Select(Normalize).ToHashSet(StringComparer.Ordinal);
            foreach (var reference in observation.CitedReferences.Select(Normalize).Distinct(StringComparer.Ordinal))
            {
                cited++;
                if (!issued.Contains(reference))
                {
                    details.Add($"{observation.CaseId}: {reference}");
                }
            }
        }

        return new UnsupportedReferenceResult(withCitations.Count, cited, details.Count, details);
    }

    /// <summary>
    /// The references used by a recommendation's raw JSON output. Malformed JSON yields the IDs found
    /// anywhere in the text, so a recommendation that failed schema validation is still checked.
    /// </summary>
    public static IReadOnlyList<string> FromRecommendationJson(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return [];
        }

        var references = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(rawOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            foreach (var list in new[] { "evidenceRefs", "policyRefs" })
            {
                if (root.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    references.AddRange(items.EnumerateArray()
                        .Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String)
                        .Select(i => i.GetProperty("ref").GetString()!));
                }
            }

            if (root.TryGetProperty("reasoningSummary", out var summary) && summary.ValueKind == JsonValueKind.String)
            {
                references.AddRange(ReferenceId().Matches(summary.GetString()!).Select(m => m.Value));
            }
        }
        catch (JsonException)
        {
            references.AddRange(ReferenceId().Matches(rawOutput).Select(m => m.Value));
        }

        return references.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string Normalize(string reference) => reference.Trim().ToUpperInvariant();

    [GeneratedRegex(@"\b(?:EV|POL|GLB)-\d+\b")]
    private static partial Regex ReferenceId();
}
