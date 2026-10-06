using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Warranty.Evaluation.Metrics;

/// <summary>One labelled extraction field compared with the agent output.</summary>
public sealed record FieldComparison(string CaseId, string Section, string Field, string Expected, string? Actual, bool Match);

public sealed record ExtractionAccuracyResult(
    Ratio Overall,
    IReadOnlyDictionary<string, Ratio> BySection,
    IReadOnlyDictionary<string, Ratio> ByField,
    IReadOnlyList<FieldComparison> Mismatches);

/// <summary>
/// Extraction field accuracy (research R19): every field listed under a case's <c>expected.extraction</c>
/// — Intake output, the invoice extraction and each photo analysis in upload order — is compared with the
/// agent's output; fields that are not listed are not scored. A missing output or field is a miss.
/// Strings compare trimmed and case-insensitively, numbers to the cent, <c>damageTypes</c> as sets, and
/// <c>invoiceDateOffsetMonths</c> as the invoice date it implies relative to the claim date.
/// </summary>
public static class ExtractionAccuracy
{
    public const string Intake = "intake";

    public const string Invoice = "invoice";

    public const string Photo = "photo";

    private const string InvoiceDateOffset = "invoiceDateOffsetMonths";

    public static ExtractionAccuracyResult Compute(IEnumerable<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var comparisons = cases.SelectMany(c => Compare(c.CaseId, c.Expected.Extraction, c.Observation)).ToList();
        return new ExtractionAccuracyResult(
            comparisons.Aggregate(Ratio.Empty, (ratio, c) => ratio.Add(c.Match)),
            Ratio.By(comparisons, c => SectionKind(c.Section), c => c.Match),
            Ratio.By(comparisons, c => $"{SectionKind(c.Section)}.{c.Field}", c => c.Match),
            comparisons.Where(c => !c.Match).ToList());
    }

    /// <summary>The comparisons of one case; sections are <c>intake</c>, <c>invoice</c> and <c>photo[i]</c> (0-based).</summary>
    public static IReadOnlyList<FieldComparison> Compare(string caseId, JsonObject? expected, CaseObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var result = new List<FieldComparison>();
        if (expected is null)
        {
            return result;
        }

        if (expected[Intake] is JsonObject intake)
        {
            result.AddRange(CompareSection(caseId, Intake, intake, observation.IntakeExtraction, observation.ClaimDate));
        }

        if (expected[Invoice] is JsonObject invoice)
        {
            result.AddRange(CompareSection(caseId, Invoice, invoice, observation.InvoiceExtraction, observation.ClaimDate));
        }

        if (expected["photos"] is JsonArray photos)
        {
            for (var i = 0; i < photos.Count; i++)
            {
                if (photos[i] is JsonObject photo)
                {
                    var actual = i < observation.PhotoAnalyses.Count ? observation.PhotoAnalyses[i] : null;
                    result.AddRange(CompareSection(caseId, $"{Photo}[{i}]", photo, actual, observation.ClaimDate));
                }
            }
        }

        return result;
    }

    /// <summary>Whether an agent value matches a labelled value under the rules above.</summary>
    public static bool ValuesMatch(JsonNode? expected, JsonNode? actual)
    {
        if (expected is null)
        {
            return actual is null || actual.GetValueKind() == JsonValueKind.Null;
        }

        if (actual is null)
        {
            return false;
        }

        if (expected is JsonArray expectedItems)
        {
            return actual is JsonArray actualItems && SetOf(expectedItems).SetEquals(SetOf(actualItems));
        }

        return expected.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False
                => actual.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                   && expected.GetValue<bool>() == actual.GetValue<bool>(),
            JsonValueKind.Number
                => actual.GetValueKind() == JsonValueKind.Number
                   && Math.Abs(expected.GetValue<decimal>() - actual.GetValue<decimal>()) < 0.005m,
            JsonValueKind.String
                => actual.GetValueKind() == JsonValueKind.String
                   && string.Equals(expected.GetValue<string>().Trim(), actual.GetValue<string>().Trim(), StringComparison.OrdinalIgnoreCase),
            _ => JsonNode.DeepEquals(expected, actual),
        };
    }

    private static IEnumerable<FieldComparison> CompareSection(
        string caseId, string section, JsonObject expected, JsonObject? actual, DateOnly claimDate)
    {
        foreach (var (name, value) in expected)
        {
            var field = name;
            var expectedValue = value;
            if (name == InvoiceDateOffset && value is not null)
            {
                // Labelled relative to the claim date, like the purchase date; the agent reads an ISO date.
                field = "invoiceDate";
                expectedValue = JsonValue.Create(
                    claimDate.AddMonths(value.GetValue<int>()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            var actualValue = actual?[field];
            yield return new FieldComparison(
                caseId, section, field, Display(expectedValue) ?? "null", Display(actualValue), ValuesMatch(expectedValue, actualValue));
        }
    }

    private static HashSet<string> SetOf(JsonArray items)
        => items.Select(i => Display(i)?.Trim('"') ?? "null").ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? Display(JsonNode? node) => node?.ToJsonString();

    private static string SectionKind(string section) => section.StartsWith(Photo, StringComparison.Ordinal) ? Photo : section;
}
