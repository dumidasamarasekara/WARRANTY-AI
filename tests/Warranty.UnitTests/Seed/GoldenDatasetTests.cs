using System.Text.Json;
using System.Text.RegularExpressions;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.UnitTests.Seed;

/// <summary>
/// The golden dataset (<c>seed/golden/golden-claims.json</c>, T107) must have the composition SC-005 and
/// research R19 require, reference only seeded catalogs, customers and policy clauses, and carry labels
/// that are consistent with the tenant thresholds and the guardrail dispositions.
/// </summary>
public sealed partial class GoldenDatasetTests
{
    private static readonly string[] Tenants = ["aurora", "borealis"];

    private static readonly string[] Recommendations = ["APPROVE", "REJECT", "REQUEST_MORE_INFORMATION", "HUMAN_REVIEW"];

    private static readonly Dictionary<Disposition, ClaimStatus> StatusByDisposition = new()
    {
        [Disposition.AutoApprove] = ClaimStatus.Approved,
        [Disposition.AutoReject] = ClaimStatus.Rejected,
        [Disposition.RequestInformation] = ClaimStatus.PendingInformation,
        [Disposition.HumanReview] = ClaimStatus.UnderReview,
    };

    private static readonly HashSet<string> RequestedItems = new(StringComparer.Ordinal)
    {
        "INVOICE", "LEGIBLE_INVOICE", "PHOTO_OF_DAMAGE", "PHOTO_OF_SERIAL_LABEL", "PURCHASE_DATE", "PROBLEM_DETAILS", "OTHER",
    };

    [Fact]
    public void Each_tenant_has_at_least_20_cases_and_4_per_expected_recommendation()
    {
        var cases = LoadCases();
        foreach (var tenant in Tenants)
        {
            var own = cases.Where(c => Text(c, "tenant") == tenant).ToList();
            own.Count.ShouldBeGreaterThanOrEqualTo(20, $"{tenant}: SC-005 needs at least 20 labelled claims");
            foreach (var recommendation in Recommendations)
            {
                own.Count(c => Expected(c).GetProperty("recommendation").ValueKind == JsonValueKind.String
                               && Text(Expected(c), "recommendation") == recommendation)
                    .ShouldBeGreaterThanOrEqualTo(4, $"{tenant}: at least 4 cases expecting {recommendation}");
            }
        }

        cases.Select(c => Text(c, "caseId")).ShouldBeUnique();
        cases.ShouldAllBe(c => CaseId().IsMatch(Text(c, "caseId")));
    }

    [Fact]
    public void The_mix_covers_single_signal_duplicate_window_reviewer_return_and_request_limit_cases()
    {
        var cases = LoadCases();
        foreach (var tenant in Tenants)
        {
            var own = cases.Where(c => Text(c, "tenant") == tenant).ToList();
            own.ShouldContain(c => Strings(Expected(c), "riskSignals").Count == 1 && Text(Expected(c), "riskLevel") == "Medium", $"{tenant}: single signal");
            own.ShouldContain(c => Strings(Expected(c), "riskSignals").Contains("DUPLICATE_SERIAL_CLAIM") && HasHistory(c), $"{tenant}: duplicate window");
            own.Any(c => Reasons(c).Contains("RETURNED_AFTER_REVIEWER_REQUEST") && Setup(c, "reviewerInfoRequested") is { ValueKind: JsonValueKind.True })
                .ShouldBeTrue($"{tenant}: reviewer return");
            own.Any(c => Reasons(c).Contains("INFO_INCOMPLETE_AFTER_2_REQUESTS") && Setup(c, "autoInfoRequestCount") is { } count && count.GetInt32() == 2)
                .ShouldBeTrue($"{tenant}: request limit");
        }

        var reasons = cases.SelectMany(Reasons).ToHashSet(StringComparer.Ordinal);
        string[] required = ["VALUE_ABOVE_LIMIT", "ALWAYS_REVIEW_CATEGORY", "RISK_MEDIUM", "RISK_HIGH", "EVIDENCE_CONFLICT", "AI_RECOMMENDS_REVIEW",
            "NO_APPLICABLE_POLICY", "PRODUCT_NOT_IN_CATALOG", "RETURNED_AFTER_REVIEWER_REQUEST", "INFO_INCOMPLETE_AFTER_2_REQUESTS"];
        required.ShouldBeSubsetOf(reasons);

        // SC-010: one manipulation attempt per placement (description, invoice text, photo text).
        cases.ShouldContain(c => Expected(c).GetProperty("extraction").GetProperty("intake").GetProperty("containsInstructionsToSystem").GetBoolean());
        EvidenceItems().Any(i => i.TryGetProperty("note", out _)).ShouldBeTrue("an instruction printed on an invoice");
        EvidenceItems().Any(i => i.TryGetProperty("sticker", out _)).ShouldBeTrue("an instruction visible in a photo");
    }

    [Fact]
    public void Every_case_references_its_tenants_seeded_serials_customers_and_policy_clauses()
    {
        var scenarioSerials = ScenarioSerials();
        var cases = LoadCases();
        cases.Select(c => $"{Text(c, "tenant")}/{Text(c, "serial")}").ShouldBeUnique("a serial picks the replay fixtures, so each golden case needs its own");
        foreach (var c in cases)
        {
            var id = Text(c, "caseId");
            var tenant = Text(c, "tenant");
            var serial = Text(c, "serial");
            var claim = c.GetProperty("claim");
            var model = Text(claim.GetProperty("product"), "modelCode");

            id.ShouldStartWith(tenant == "aurora" ? "G-AUR-" : "G-BOR-");
            Text(claim.GetProperty("product"), "serialNumber").ShouldBe(serial, $"{id}: claim serial");
            scenarioSerials.ShouldNotContain($"{tenant}/{serial}", $"{id}: {serial} is used by a scenario in scenarios.json");

            var catalogSerial = Json(tenant, "serials.json").EnumerateArray().FirstOrDefault(s => Text(s, "serialNumber") == serial);
            var product = Json(tenant, "products.json").EnumerateArray().FirstOrDefault(p => Text(p, "modelCode") == model);
            if (c.TryGetProperty("notInCatalog", out var notInCatalog) && notInCatalog.GetBoolean())
            {
                catalogSerial.ValueKind.ShouldBe(JsonValueKind.Undefined, $"{id}: {serial} must not be seeded");
                product.ValueKind.ShouldBe(JsonValueKind.Undefined, $"{id}: {model} must not be seeded");
            }
            else
            {
                catalogSerial.ValueKind.ShouldBe(JsonValueKind.Object, $"{id}: {serial} is not in the {tenant} catalog");
                Text(catalogSerial, "modelCode").ShouldBe(model, $"{id}: model of {serial}");
                catalogSerial.TryGetProperty("reservedFor", out var reserved).ShouldBeTrue($"{id}: {serial} is not reserved");
                reserved.GetString().ShouldBe($"golden {id}");
            }

            var email = Text(claim.GetProperty("customer"), "email");
            Json(tenant, "customers.json").EnumerateArray().ShouldContain(x => Text(x, "email") == email, $"{id}: unknown customer");

            var hasOffset = c.TryGetProperty("purchaseDateOffsetMonths", out var offset) && offset.GetInt32() < 0;
            var hasDate = c.TryGetProperty("purchaseDate", out _);
            (hasOffset ^ hasDate).ShouldBeTrue($"{id}: needs exactly one of purchaseDateOffsetMonths (< 0) or purchaseDate");
            claim.GetProperty("purchase").TryGetProperty("date", out _).ShouldBeFalse($"{id}: purchase.date is computed");

            var clauses = TenantClauseKeys(tenant);
            Strings(Expected(c), "citedClauseKeys").ShouldAllBe(k => clauses.Contains(k), $"{id}: a cited clause is not declared by a {tenant} policy");

            if (Setup(c, "history") is { } history)
            {
                history.EnumerateArray().ShouldAllBe(h => Text(h, "serialNumber") == serial && Text(h, "customerEmail") == email, $"{id}: history must concern the case's serial");
            }
        }
    }

    [Fact]
    public void Every_case_has_its_evidence_and_every_generated_file_belongs_to_a_case()
    {
        var root = Path.Combine(RepositoryRoot(), "seed", "evidence");
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in LoadCases())
        {
            var id = Text(c, "caseId");
            var evidence = c.GetProperty("evidence");
            var files = evidence.GetProperty("photos").EnumerateArray().Select(p => p.GetString()!).ToList();
            if (evidence.GetProperty("invoice").ValueKind == JsonValueKind.String)
            {
                files.Add(evidence.GetProperty("invoice").GetString()!);
            }

            files.ShouldNotBeEmpty($"{id}: no evidence");
            evidence.GetProperty("photos").GetArrayLength().ShouldBeInRange(0, 8);
            foreach (var file in files)
            {
                file.StartsWith($"golden/{id}/", StringComparison.Ordinal).ShouldBeTrue($"{id}: {file} must live in golden/{id}/");
                File.Exists(Path.Combine(root, file)).ShouldBeTrue($"{id}: {file} is missing (run the evidence generator)");
                referenced.Add(file);
            }

            Expected(c).GetProperty("extraction").GetProperty("photos").GetArrayLength()
                .ShouldBe(evidence.GetProperty("photos").GetArrayLength(), $"{id}: one expected photo analysis per photo");
        }

        var generated = EvidenceItems().Select(i => $"golden/{Text(i, "path")}").ToList();
        generated.ShouldBeUnique();
        generated.ToHashSet(StringComparer.Ordinal).SetEquals(referenced).ShouldBeTrue("seed/evidence/golden/evidence.json and the cases must name the same files");
    }

    [Fact]
    public void Labels_are_consistent_with_the_dispositions_and_the_tenant_thresholds()
    {
        foreach (var c in LoadCases())
        {
            var id = Text(c, "caseId");
            var tenant = Text(c, "tenant");
            var expected = Expected(c);
            var disposition = Enum.Parse<Disposition>(Text(expected, "disposition"));
            Enum.Parse<ClaimStatus>(Text(expected, "status")).ShouldBe(StatusByDisposition[disposition], $"{id}: status");
            var risk = Enum.Parse<RiskLevel>(Text(expected, "riskLevel"));
            var signals = Strings(expected, "riskSignals");
            var reasons = Reasons(c);
            signals.All(s => WireName.TryParse<RiskSignalCode>(s, out _)).ShouldBeTrue($"{id}: unknown signal");
            reasons.All(r => WireName.TryParse<EscalationReason>(r, out _)).ShouldBeTrue($"{id}: unknown escalation reason");
            Strings(expected, "requestedItems").ShouldAllBe(i => RequestedItems.Contains(i), $"{id}: unknown requested item");
            (risk == RiskLevel.Low).ShouldBe(signals.Count == 0, $"{id}: risk is Low exactly when no signal is present (R23)");

            var recommendation = expected.GetProperty("recommendation");
            if (recommendation.ValueKind == JsonValueKind.Null)
            {
                disposition.ShouldBe(Disposition.RequestInformation, $"{id}: only the intake short-circuit has no recommendation");
            }
            else
            {
                Recommendations.ShouldContain(recommendation.GetString()!);
            }

            var settings = Json(tenant, "tenant.json").GetProperty("settings");
            var product = Json(tenant, "products.json").EnumerateArray()
                .FirstOrDefault(p => Text(p, "modelCode") == Text(c.GetProperty("claim").GetProperty("product"), "modelCode"));
            var aboveLimit = product.ValueKind == JsonValueKind.Object && product.GetProperty("claimValue").GetDecimal() > settings.GetProperty("autoApprovalLimit").GetDecimal();
            reasons.Contains("VALUE_ABOVE_LIMIT").ShouldBe(aboveLimit, $"{id}: VALUE_ABOVE_LIMIT iff the product's claim value exceeds the limit");
            var alwaysReview = product.ValueKind == JsonValueKind.Object
                && settings.GetProperty("alwaysReviewCategories").EnumerateArray().Any(x => x.GetString() == Text(product, "category"));
            reasons.Contains("ALWAYS_REVIEW_CATEGORY").ShouldBe(alwaysReview, $"{id}: ALWAYS_REVIEW_CATEGORY iff the category is always reviewed");
            if (risk != RiskLevel.Low)
            {
                reasons.ShouldContain(risk == RiskLevel.High ? "RISK_HIGH" : "RISK_MEDIUM", $"{id}: risk reason");
            }

            switch (disposition)
            {
                case Disposition.AutoApprove or Disposition.AutoReject:
                    recommendation.GetString().ShouldBe(disposition == Disposition.AutoApprove ? "APPROVE" : "REJECT", $"{id}: automatic decision follows the recommendation");
                    reasons.ShouldBeEmpty($"{id}: an automatic decision has no escalation reason");
                    Strings(expected, "citedClauseKeys").ShouldNotBeEmpty($"{id}: an automatic decision is grounded in a clause");
                    Text(expected, "coverage").ShouldBe(disposition == Disposition.AutoApprove ? "COVERED" : "NOT_COVERED", $"{id}: coverage");
                    break;
                case Disposition.RequestInformation:
                    reasons.ShouldBeEmpty($"{id}");
                    Strings(expected, "requestedItems").ShouldNotBeEmpty($"{id}: a request names the missing items");
                    break;
                case Disposition.HumanReview:
                    reasons.ShouldNotBeEmpty($"{id}: an escalation names its reasons");
                    break;
            }

            if (recommendation.ValueKind == JsonValueKind.String && recommendation.GetString() == "HUMAN_REVIEW")
            {
                disposition.ShouldBe(Disposition.HumanReview, $"{id}: an AI HUMAN_REVIEW is never finalized");
            }
        }
    }

    private static bool HasHistory(JsonElement c) => Setup(c, "history") is { ValueKind: JsonValueKind.Array } history && history.GetArrayLength() > 0;

    private static JsonElement? Setup(JsonElement c, string property)
        => c.TryGetProperty("setup", out var setup) && setup.TryGetProperty(property, out var value) ? value : null;

    private static JsonElement Expected(JsonElement c) => c.GetProperty("expected");

    private static List<string> Reasons(JsonElement c) => Strings(Expected(c), "escalationReasons");

    private static List<string> Strings(JsonElement element, string property)
        => element.GetProperty(property).EnumerateArray().Select(x => x.GetString()!).ToList();

    private static string Text(JsonElement element, string property) => element.GetProperty(property).GetString()!;

    private static List<JsonElement> LoadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "seed", "golden", "golden-claims.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToList();
    }

    private static List<JsonElement> EvidenceItems()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "seed", "evidence", "golden", "evidence.json")));
        return document.RootElement.GetProperty("files").EnumerateArray().Select(c => c.Clone()).ToList();
    }

    private static HashSet<string> ScenarioSerials()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "seed", "golden", "scenarios.json")));
        return document.RootElement.GetProperty("scenarios").EnumerateArray()
            .Select(s => $"{Text(s, "tenant")}/{Text(s, "serial")}")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static JsonElement Json(string tenant, string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "seed", "tenants", tenant, file)));
        return document.RootElement.Clone();
    }

    /// <summary>Clause keys declared in the front matter of the tenant's policy files.</summary>
    private static HashSet<string> TenantClauseKeys(string tenant)
        => Directory.GetFiles(Path.Combine(RepositoryRoot(), "seed", "tenants", tenant, "policies"), "*.md")
            .SelectMany(file => ClauseDeclaration().Matches(File.ReadAllText(file)).Select(m => m.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

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

    [GeneratedRegex(@"^G-(AUR|BOR)-\d{2}$")]
    private static partial Regex CaseId();

    [GeneratedRegex(@"^\s+(?<key>[A-Z][A-Z0-9]*-[A-Z0-9-]*?\d+(?:\.\d+)*):\s*\{\s*type:", RegexOptions.Multiline)]
    private static partial Regex ClauseDeclaration();
}
