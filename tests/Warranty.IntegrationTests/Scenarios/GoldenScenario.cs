using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// One entry of <c>seed/golden/scenarios.json</c> and the multipart submission it describes
/// (contracts/rest-api.openapi.yaml, <c>ClaimSubmissionForm</c>: a <c>claim</c> JSON part, an
/// <c>invoice</c> part and one <c>photos</c> part per photo, evidence read from <c>seed/evidence/</c>).
/// The purchase date is computed from today's date (UTC, the claim date of a submission made now), so a
/// scenario holds on whatever day it runs; the replay fixtures carry <c>{{claim.purchaseDate}}</c> for it.
/// </summary>
public sealed class GoldenScenario
{
    private static readonly Lazy<IReadOnlyList<JsonObject>> All = new(LoadAll);

    private readonly JsonObject _entry;

    private GoldenScenario(JsonObject entry) => _entry = entry;

    public string ScenarioId => (string)_entry["scenarioId"]!;

    public string Tenant => (string)_entry["tenant"]!;

    public string Serial => (string)_entry["serial"]!;

    /// <summary>The contact e-mail given at submission, used for claimant access.</summary>
    public string ContactEmail => (string)_entry["claim"]!["customer"]!["email"]!;

    public JsonObject Expected => _entry["expected"]!.AsObject();

    /// <summary>The policy clause keys the recommendation is expected to rely on.</summary>
    public IReadOnlyList<string> ExpectedClauseKeys
        => Expected["citedClauseKeys"]?.AsArray().Select(k => (string)k!).ToList() ?? [];

    /// <summary>The claimant channel host of the scenario's tenant, e.g. <c>aurora.localhost</c>.</summary>
    public string ChannelHost => $"{Tenant}.localhost";

    /// <summary>True when a staff user submits the claim through <c>POST /api/claims</c> instead of the claimant channel.</summary>
    public bool IsAgentChannel => (string?)_entry["channel"] == "agent";

    /// <summary>The staff user who submits an agent-channel claim: <c>submittedBy</c>, by default <c>agent.{tenant}</c>.</summary>
    public string SubmittedBy => (string?)_entry["submittedBy"] ?? $"agent.{Tenant}";

    /// <summary>The earlier scenario whose claim this one acts on instead of submitting its own (<c>claimOf</c>), if any.</summary>
    public string? ClaimOf => (string?)_entry["claimOf"];

    /// <summary>The staff user who records <see cref="ReviewerDecision"/>, if the scenario has one.</summary>
    public string? Reviewer => (string?)_entry["reviewer"];

    /// <summary>
    /// The reviewer's decision as a <c>ReviewDecisionRequest</c> body (contracts/rest-api.openapi.yaml): a fresh copy,
    /// so a test may remove or change members to send an invalid variant.
    /// </summary>
    public JsonObject ReviewerDecision
        => _entry["reviewerDecision"]?.DeepClone().AsObject()
           ?? throw new InvalidOperationException($"Scenario '{ScenarioId}' has no reviewerDecision.");

    /// <summary>The claim status the scenario's claim is expected to reach.</summary>
    public string? ExpectedStatus => (string?)Expected["status"];

    /// <summary>The escalation reason codes the claim's latest run must include.</summary>
    public IReadOnlyList<string> ExpectedEscalationReasons => ExpectedList("escalationReasons");

    /// <summary>Staff users whose review queue must list the claim.</summary>
    public IReadOnlyList<string> ReviewQueues => ExpectedList("reviewQueues");

    /// <summary>Staff users whose review queue must not list the claim.</summary>
    public IReadOnlyList<string> AbsentFromReviewQueues => ExpectedList("absentFromReviewQueues");

    /// <summary>The staff user whose decision on the claim is refused as a self-review (separation of duties), if any.</summary>
    public string? SelfReviewRefusedFor => (string?)Expected["selfReviewRefusedFor"];

    /// <summary>Security event kinds the scenario must write, e.g. <c>SELF_REVIEW_REFUSED</c>.</summary>
    public IReadOnlyList<string> ExpectedSecurityEvents => ExpectedList("securityEvents");

    /// <summary>Whether the reviewer's decision is expected to override the AI recommendation, if the scenario says.</summary>
    public bool? ExpectedOverridesAi => (bool?)Expected["overridesAi"];

    public static GoldenScenario Load(string scenarioId)
        => new(All.Value.SingleOrDefault(s => (string?)s["scenarioId"] == scenarioId)?.DeepClone().AsObject()
               ?? throw new ArgumentException($"Scenario '{scenarioId}' is not in seed/golden/scenarios.json.", nameof(scenarioId)));

    /// <summary>
    /// A variant with its own serial and evidence files (paths under <c>seed/evidence/</c>), for a test that
    /// needs a claim of its own: claims sharing a serial or an evidence file would raise duplicate-serial or
    /// evidence-reuse signals in each other's adjudication.
    /// </summary>
    public GoldenScenario With(string scenarioId, string serial, string invoice, IReadOnlyList<string> photos)
    {
        var entry = _entry.DeepClone().AsObject();
        entry["scenarioId"] = scenarioId;
        entry["serial"] = serial;
        entry["claim"]!["product"]!["serialNumber"] = serial;
        entry["evidence"] = new JsonObject
        {
            ["invoice"] = invoice,
            ["photos"] = new JsonArray(photos.Select(p => (JsonNode)p).ToArray()),
        };
        return new GoldenScenario(entry);
    }

    /// <summary>The purchase date for a claim submitted on <paramref name="claimDate"/>.</summary>
    public DateOnly PurchaseDate(DateOnly claimDate)
        => _entry["purchaseDate"] is { } absolute
            ? DateOnly.ParseExact((string)absolute!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : claimDate.AddMonths((int)_entry["purchaseDateOffsetMonths"]!);

    /// <summary>
    /// The submission as sent to <c>POST /api/public/claims</c> or <c>POST /api/claims</c> for a claim
    /// submitted today (UTC). <paramref name="adjust"/> may change the claim JSON, e.g. the purchase date.
    /// </summary>
    public MultipartFormDataContent ToSubmission(Action<JsonObject>? adjust = null)
    {
        var claim = _entry["claim"]!.DeepClone().AsObject();
        claim["purchase"]!["date"] = PurchaseDate(DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        adjust?.Invoke(claim);

        var form = new MultipartFormDataContent();
        var json = new StringContent(claim.ToJsonString(), Encoding.UTF8);
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(json, "claim");

        var evidence = _entry["evidence"]!;
        if (evidence["invoice"] is { } invoice)
        {
            form.Add(EvidencePart((string)invoice!), "invoice", Path.GetFileName((string)invoice!));
        }

        foreach (var photo in evidence["photos"]!.AsArray().Select(p => (string)p!))
        {
            form.Add(EvidencePart(photo), "photos", Path.GetFileName(photo));
        }

        return form;
    }

    private IReadOnlyList<string> ExpectedList(string name)
        => Expected[name]?.AsArray().Select(v => (string)v!).ToList() ?? [];

    private static ByteArrayContent EvidencePart(string relativePath)
    {
        var content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(RepositoryRoot(), "seed", "evidence", relativePath)));
        content.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        });
        return content;
    }

    private static IReadOnlyList<JsonObject> LoadAll()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "seed", "golden", "scenarios.json")))!;
        return root["scenarios"]!.AsArray().Select(s => s!.AsObject()).ToList();
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Warranty.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Warranty.slnx) not found.");
    }
}
