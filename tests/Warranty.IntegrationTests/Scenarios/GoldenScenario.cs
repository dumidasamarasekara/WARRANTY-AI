using System.Collections.Concurrent;
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

    /// <summary>Claims submitted once per scenario and test run, shared by every test class (<see cref="SubmitOnceAsync"/>).</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<Guid>>> Submitted = new(StringComparer.Ordinal);

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

    /// <summary>The scenario's expectations (of its round-1 outcome, or of <see cref="Round"/>) with typed accessors.</summary>
    public ScenarioExpectation Outcome => new(Expected);

    /// <summary>The item codes the claim asks the submitter for (<c>expected.requestedItems</c>).</summary>
    public IReadOnlyList<string> RequestedItems => Outcome.RequestedItems;

    /// <summary>The claim round the scenario describes (<c>round</c>, on a <c>claimOf</c> scenario); 1 by default.</summary>
    public int Round => (int?)_entry["round"] ?? 1;

    /// <summary>The files the submitter supplies to start <see cref="Round"/> (<c>supplement</c>), if any.</summary>
    public ScenarioSupplement? Supplement => _entry["supplement"] is JsonObject supplement ? new ScenarioSupplement(supplement) : null;

    /// <summary>Later rounds of the scenario's own claim (<c>rounds</c>), in round order.</summary>
    public IReadOnlyList<ScenarioRound> Rounds
        => _entry["rounds"]?.AsArray()
               .Select(r => r!.AsObject())
               .Select(r => new ScenarioRound(
                   (int)r["round"]!,
                   new ScenarioSupplement(r["supplement"]!.AsObject()),
                   new ScenarioExpectation(r["expected"]!.AsObject())))
               .OrderBy(r => r.Round)
               .ToList()
           ?? [];

    /// <summary>False when the scenario's own evidence has no invoice (a submission the claim submission API refuses).</summary>
    public bool HasInvoice => _entry["evidence"]?["invoice"] is not null;

    /// <summary>The scenario's photo files, paths under <c>seed/evidence/</c>, in upload order.</summary>
    public IReadOnlyList<string> PhotoFiles => _entry["evidence"]!["photos"]!.AsArray().Select(p => (string)p!).ToList();

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

    /// <summary>Scenarios whose claims must be submitted (and settled) before this one, e.g. the claim whose photo is reused.</summary>
    public IReadOnlyList<string> SubmitAfter => List(_entry, "submitAfter");

    /// <summary>True when the replayed model is deliberately wrong, so <see cref="ExpectedRecommendation"/> is what the fixture recommends.</summary>
    public bool FaultyRecommendation => (bool?)_entry["faultyRecommendation"] ?? false;

    /// <summary>Fixture files whose structured output deliberately violates the output schema.</summary>
    public IReadOnlyList<string> InvalidOutputFixtures => List(_entry, "invalidOutputFixtures");

    /// <summary>Tools outside the agent's scope that a fixture deliberately requests.</summary>
    public IReadOnlyList<string> DeniedToolRequests => List(_entry, "deniedToolRequests");

    /// <summary>The expected disposition (data-model wire name, e.g. <c>HumanReview</c>), if the scenario says.</summary>
    public string? ExpectedDisposition => (string?)Expected["disposition"];

    /// <summary>
    /// The decision of the run's valid recommendation (e.g. <c>APPROVE</c>), or null when no valid recommendation
    /// exists — <c>"recommendation": null</c>. <see cref="HasExpectedRecommendation"/> tells whether the scenario says.
    /// </summary>
    public string? ExpectedRecommendation => (string?)Expected["recommendation"];

    /// <summary>True when the scenario states <c>recommendation</c> (a decision or null).</summary>
    public bool HasExpectedRecommendation => Expected.ContainsKey("recommendation");

    /// <summary>False when an invalid recommendation is expected to be stored, if the scenario says.</summary>
    public bool? ExpectedRecommendationValid => (bool?)Expected["recommendationValid"];

    /// <summary>The computed risk level (<c>Low</c>, <c>Medium</c>, <c>High</c>), if the scenario says.</summary>
    public string? ExpectedRiskLevel => (string?)Expected["riskLevel"];

    /// <summary>The computed risk score, if the scenario says.</summary>
    public int? ExpectedRiskScore => (int?)Expected["riskScore"];

    /// <summary>The risk level the model reported in its decision output (never used by the guardrails), if the scenario says.</summary>
    public string? ExpectedModelRiskLevel => (string?)Expected["modelRiskLevel"];

    /// <summary>Risk signal codes the claim must have.</summary>
    public IReadOnlyList<string> ExpectedRiskSignals => ExpectedList("riskSignals");

    /// <summary>Risk signal codes the claim must not have.</summary>
    public IReadOnlyList<string> AbsentRiskSignals => ExpectedList("absentRiskSignals");

    /// <summary>Guardrail checks that must fail (others may fail too).</summary>
    public IReadOnlyList<string> FailedGuardrails => ExpectedList("failedGuardrails");

    /// <summary>Guardrail checks that must pass.</summary>
    public IReadOnlyList<string> PassedGuardrails => ExpectedList("passedGuardrails");

    /// <summary>Agents recorded as <c>AiStepFailed</c>, with the run's <c>failure_reason</c>.</summary>
    public IReadOnlyList<string> FailedAiSteps => ExpectedList("failedAiSteps");

    /// <summary>Model calls per agent the run makes, including corrective turns and retries.</summary>
    public IReadOnlyDictionary<string, int> ExpectedModelCalls
        => Expected["modelCalls"]?.AsObject().ToDictionary(p => p.Key, p => (int)p.Value!, StringComparer.Ordinal)
           ?? new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Invoice fields whose consistency check fails (empty: every field matches), or null when the scenario does not say.</summary>
    public IReadOnlyList<string>? ExpectedInvoiceMismatches
        => Expected.ContainsKey("invoiceMismatches") ? ExpectedList("invoiceMismatches") : null;

    /// <summary>The status text of the claimant view (e.g. <c>Under Review</c>), if the scenario says.</summary>
    public string? ExpectedClaimantStatus => (string?)Expected["claimantStatus"];

    /// <summary>True when the claimant view must show no risk vocabulary.</summary>
    public bool ClaimantViewHidesRisk => (bool?)Expected["claimantViewHidesRisk"] ?? false;

    /// <summary>False when no repair request may exist for the claim, if the scenario says.</summary>
    public bool? ExpectedRepairRequestCreated => (bool?)Expected["repairRequestCreated"];

    /// <summary>The trail steps the claim's trail ends with (before any executed actions).</summary>
    public IReadOnlyList<string> ExpectedLastTrailEntries => ExpectedList("lastTrailEntries");

    /// <summary>
    /// Submits the scenario's claim at most once per test run, across every test class, and returns its claim ID:
    /// a second claim for the same serial or evidence would raise duplicate-serial or evidence-reuse signals in
    /// its own adjudication, so scenario classes that share a scenario (e.g. S4-aurora) share its claim.
    /// </summary>
    public Task<Guid> SubmitOnceAsync(Func<GoldenScenario, Task<Guid>> submit)
        => Submitted.GetOrAdd(ScenarioId, _ => new Lazy<Task<Guid>>(() => submit(this))).Value;

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
        var claim = ClaimJson();
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

    /// <summary>The <c>claim</c> JSON (<c>ClaimSubmissionData</c>) of a claim submitted today (UTC), with its purchase date.</summary>
    public JsonObject ClaimJson()
    {
        var claim = _entry["claim"]!.DeepClone().AsObject();
        claim["purchase"]!["date"] = PurchaseDate(DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return claim;
    }

    /// <summary>The bytes of an evidence file under <c>seed/evidence/</c>.</summary>
    public static byte[] EvidenceBytes(string relativePath)
        => File.ReadAllBytes(Path.Combine(RepositoryRoot(), "seed", "evidence", relativePath));

    private IReadOnlyList<string> ExpectedList(string name) => List(Expected, name);

    private static IReadOnlyList<string> List(JsonObject owner, string name)
        => owner[name]?.AsArray().Select(v => (string)v!).ToList() ?? [];

    /// <summary>A multipart part with an evidence file under <c>seed/evidence/</c>, typed by its extension.</summary>
    internal static ByteArrayContent EvidencePart(string relativePath)
    {
        var content = new ByteArrayContent(EvidenceBytes(relativePath));
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

/// <summary>
/// An <c>expected</c> object of a scenario or of one of its <c>rounds</c> (the <c>$comment</c> of
/// <c>seed/golden/scenarios.json</c> documents every key). List accessors return an empty list for a missing key;
/// use <see cref="Has"/> where "missing" and "empty" mean different things (e.g. <c>riskSignals</c>).
/// </summary>
public sealed class ScenarioExpectation(JsonObject expected)
{
    public string? Status => (string?)expected["status"];

    public string? Disposition => (string?)expected["disposition"];

    public string? Recommendation => (string?)expected["recommendation"];

    public string? Coverage => (string?)expected["coverage"];

    public string? RiskLevel => (string?)expected["riskLevel"];

    public string? DecidedBy => (string?)expected["decidedBy"];

    /// <summary>The claim's automatic information-request count after the round.</summary>
    public int? AutoInfoRequestCount => (int?)expected["autoInfoRequestCount"];

    /// <summary>The label of the escalation reason shown to staff.</summary>
    public string? EscalationReasonText => (string?)expected["escalationReasonText"];

    /// <summary>The item codes the claim asks the submitter for.</summary>
    public IReadOnlyList<string> RequestedItems => List("requestedItems");

    /// <summary>The only agents whose model is called in the round.</summary>
    public IReadOnlyList<string> ModelCalls => List("modelCalls");

    /// <summary>The agents recorded as failed (<c>AiStepFailed</c>).</summary>
    public IReadOnlyList<string> AiStepFailed => List("aiStepFailed");

    /// <summary>The risk signal codes the run records (empty: none).</summary>
    public IReadOnlyList<string> RiskSignals => List("riskSignals");

    public IReadOnlyList<string> FailedGuardrails => List("failedGuardrails");

    public IReadOnlyList<string> PassedGuardrails => List("passedGuardrails");

    public IReadOnlyList<string> EscalationReasons => List("escalationReasons");

    public IReadOnlyList<string> ReviewQueues => List("reviewQueues");

    public IReadOnlyList<string> CitedClauseKeys => List("citedClauseKeys");

    public IReadOnlyList<string> LastTrailEntries => List("lastTrailEntries");

    /// <summary>The rounds the claim's adjudication runs cover.</summary>
    public IReadOnlyList<int> RunRounds => expected["runRounds"]?.AsArray().Select(r => (int)r!).ToList() ?? [];

    public bool Has(string key) => expected.ContainsKey(key);

    private IReadOnlyList<string> List(string name) => expected[name]?.AsArray().Select(v => (string)v!).ToList() ?? [];
}

/// <summary>
/// The files a submitter supplies to start a later round (<c>supplement</c>: same shape as <c>evidence</c>),
/// sent as a <c>SupplementForm</c> (contracts/rest-api.openapi.yaml): an optional <c>note</c>, an <c>invoice</c>
/// part and one <c>photos</c> part per photo.
/// </summary>
public sealed class ScenarioSupplement(JsonObject supplement)
{
    public string? Invoice => (string?)supplement["invoice"];

    public IReadOnlyList<string> Photos => supplement["photos"]?.AsArray().Select(p => (string)p!).ToList() ?? [];

    public MultipartFormDataContent ToForm(string? note = null)
    {
        var form = new MultipartFormDataContent();
        if (note is not null)
        {
            form.Add(new StringContent(note), "note");
        }

        if (Invoice is { } invoice)
        {
            form.Add(GoldenScenario.EvidencePart(invoice), "invoice", Path.GetFileName(invoice));
        }

        foreach (var photo in Photos)
        {
            form.Add(GoldenScenario.EvidencePart(photo), "photos", Path.GetFileName(photo));
        }

        return form;
    }
}

/// <summary>A later round of a scenario's own claim (<c>rounds[]</c>): the supplement that starts it and what it must produce.</summary>
public sealed record ScenarioRound(int Round, ScenarioSupplement Supplement, ScenarioExpectation Expected);
