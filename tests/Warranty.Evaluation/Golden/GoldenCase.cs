using System.Globalization;
using System.Text.Json.Nodes;

namespace Warranty.Evaluation.Golden;

/// <summary>
/// One labelled case of <c>seed/golden/golden-claims.json</c> (T107; schema in the file's <c>$comment</c>).
/// Labels were set from the tenant's policy text before any model run (research R19) and are only read here.
/// </summary>
public sealed class GoldenCase
{
    private readonly JsonObject _entry;

    public GoldenCase(JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
        Expected = GoldenExpectation.From(entry["expected"]?.AsObject()
                                          ?? throw new FormatException($"Golden case '{CaseId}' has no 'expected'."));
    }

    public string CaseId => (string?)_entry["caseId"] ?? throw new FormatException("A golden case has no 'caseId'.");

    public string Tenant => (string)_entry["tenant"]!;

    public string Serial => (string)_entry["serial"]!;

    public string Title => (string?)_entry["title"] ?? string.Empty;

    public GoldenExpectation Expected { get; }

    /// <summary>The round under evaluation (setup.round, default 1).</summary>
    public int Round => (int?)_entry["setup"]?["round"] ?? 1;

    public bool ReviewerInfoRequested => (bool?)_entry["setup"]?["reviewerInfoRequested"] ?? false;

    public int AutoInfoRequestCount => (int?)_entry["setup"]?["autoInfoRequestCount"] ?? 0;

    /// <summary>Earlier claims to seed after the per-case history reset (shape of historical-claims.json).</summary>
    public IReadOnlyList<GoldenHistoryClaim> History
        => _entry["setup"]?["history"]?.AsArray().Select(h => GoldenHistoryClaim.From(h!.AsObject())).ToList() ?? [];

    /// <summary>Repository-relative evidence paths under <c>seed/evidence/</c>; null when the case has no invoice.</summary>
    public string? InvoicePath => (string?)_entry["evidence"]?["invoice"];

    public IReadOnlyList<string> PhotoPaths
        => _entry["evidence"]?["photos"]?.AsArray().Select(p => (string)p!).ToList() ?? [];

    /// <summary>The purchase date of a claim submitted on <paramref name="claimDate"/>.</summary>
    public DateOnly PurchaseDate(DateOnly claimDate)
        => _entry["purchaseDate"] is { } absolute
            ? DateOnly.ParseExact((string)absolute!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : claimDate.AddMonths((int)_entry["purchaseDateOffsetMonths"]!);

    /// <summary>The <c>ClaimSubmissionData</c> JSON with the purchase date for <paramref name="claimDate"/> filled in.</summary>
    public JsonObject ClaimJson(DateOnly claimDate)
    {
        var claim = _entry["claim"]!.DeepClone().AsObject();
        claim["purchase"]!["date"] = PurchaseDate(claimDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return claim;
    }

    public static IReadOnlyList<GoldenCase> Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new FormatException($"'{path}' is empty.");
        return root["cases"]!.AsArray().Select(c => new GoldenCase(c!.AsObject())).ToList();
    }
}

/// <summary>The labels of a case, as written in <c>expected</c>; enum values keep the dataset's spelling.</summary>
public sealed record GoldenExpectation(
    string? Recommendation,
    string Disposition,
    string? Status,
    string? Coverage,
    string? RiskLevel,
    IReadOnlyList<string> RiskSignals,
    IReadOnlyList<string> EscalationReasons,
    IReadOnlyList<string> RequestedItems,
    IReadOnlyList<string> CitedClauseKeys,
    JsonObject? Extraction)
{
    public static GoldenExpectation From(JsonObject expected)
    {
        static IReadOnlyList<string> List(JsonObject o, string name)
            => o[name]?.AsArray().Select(v => (string)v!).ToList() ?? [];

        return new GoldenExpectation(
            (string?)expected["recommendation"],
            (string?)expected["disposition"] ?? throw new FormatException("A golden case has no expected disposition."),
            (string?)expected["status"],
            (string?)expected["coverage"],
            (string?)expected["riskLevel"],
            List(expected, "riskSignals"),
            List(expected, "escalationReasons"),
            List(expected, "requestedItems"),
            List(expected, "citedClauseKeys"),
            expected["extraction"]?.AsObject());
    }
}

/// <summary>A finalized earlier claim; every <c>*DaysAgo</c> is a signed day offset from the claim date.</summary>
public sealed record GoldenHistoryClaim(
    string Reference,
    string CustomerEmail,
    string ModelCode,
    string SerialNumber,
    string Region,
    string PurchasePlace,
    decimal PurchasePrice,
    string ProblemDescription,
    int PurchasedDaysAgo,
    int SubmittedDaysAgo,
    int FinalizedDaysAgo,
    string Outcome,
    string Explanation)
{
    public static GoldenHistoryClaim From(JsonObject h)
        => new(
            (string)h["reference"]!,
            (string)h["customerEmail"]!,
            (string)h["modelCode"]!,
            (string)h["serialNumber"]!,
            (string)h["region"]!,
            (string)h["purchasePlace"]!,
            (decimal)h["purchasePrice"]!,
            (string)h["problemDescription"]!,
            (int)h["purchasedDaysAgo"]!,
            (int)h["submittedDaysAgo"]!,
            (int)h["finalizedDaysAgo"]!,
            (string)h["outcome"]!,
            (string)h["explanation"]!);
}
