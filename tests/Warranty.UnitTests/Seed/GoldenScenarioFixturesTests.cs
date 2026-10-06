using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Harness.Schemas;
using Warranty.Application.Abstractions.AI;
using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Seed;

/// <summary>
/// The golden scenarios (<c>seed/golden/scenarios.json</c>) and their hand-authored replay fixtures
/// (<c>tests/fixtures/ai-recordings/{scenarioId}/{agent}-{n}.json</c>) must load the way the replay
/// provider reads them, match the output schemas in contracts/schemas, and cite only the references
/// and policy clauses the scenario declares.
/// </summary>
public sealed partial class GoldenScenarioFixturesTests
{
    private static readonly Dictionary<string, string> SchemaByAgent = new(StringComparer.Ordinal)
    {
        ["intake"] = SchemaValidator.IntakeExtraction,
        ["evidence-invoice"] = SchemaValidator.InvoiceExtraction,
        ["evidence-photo"] = SchemaValidator.PhotoAnalysis,
        ["policy"] = SchemaValidator.PolicyAssessment,
        ["decision"] = SchemaValidator.DecisionRecommendation,
    };

    /// <summary>Tools each agent's model may request (contracts/agents-and-tools.md, tool catalog).</summary>
    private static readonly Dictionary<string, string[]> ToolsByAgent = new(StringComparer.Ordinal)
    {
        ["intake"] = ["customer_lookup", "product_lookup"],
        ["evidence-invoice"] = ["invoice_validation", "product_lookup"],
        ["evidence-photo"] = ["invoice_validation", "product_lookup"],
        ["policy"] = ["warranty_lookup", "search_policy_knowledge", "search_global_knowledge"],
        ["decision"] = ["claim_history_lookup", "search_global_knowledge"],
    };

    private static readonly IReadOnlyDictionary<string, string> SampleVariables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ReplayFixtureVariables.PurchaseDate] = "2026-06-04",
        [ReplayFixtureVariables.ClaimDate] = "2026-10-04",
    };

    private static readonly SchemaValidator Validator = new();

    public static TheoryData<string> ScenarioIds()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in LoadScenarios())
        {
            data.Add(scenario.GetProperty("scenarioId").GetString()!);
        }

        return data;
    }

    [Fact]
    public void The_replay_catalog_finds_every_scenario_by_its_tenant_and_serial()
    {
        var options = Options.Create(new AiGatewayOptions { Replay = { ScenariosPath = ScenariosPath() } });
        var catalog = ReplayScenarioCatalog.Load(options);
        var scenarios = LoadScenarios();

        catalog.Scenarios.Count.ShouldBe(scenarios.Count);
        catalog.Scenarios.Select(s => s.ScenarioId).ShouldBeUnique();
        foreach (var scenario in scenarios)
        {
            var id = scenario.GetProperty("scenarioId").GetString()!;
            var tenant = scenario.GetProperty("tenant").GetString()!;
            var serial = scenario.GetProperty("serial").GetString()!;
            var product = scenario.GetProperty("claim").GetProperty("product");

            product.GetProperty("serialNumber").GetString().ShouldBe(serial, $"{id}: the claim must carry the scenario serial");
            if (catalog.Find(serial, tenant) == id)
            {
                // The first scenario of a serial is the one replay selects; later ones share its fixtures on purpose.
                Directory.Exists(Path.Combine(RecordingsRoot(), id)).ShouldBeTrue($"{id}: no fixture folder");
            }

            var known = TenantSerials(tenant).FirstOrDefault(s => s.GetProperty("serialNumber").GetString() == serial);
            if (!(scenario.TryGetProperty("notInCatalog", out var notInCatalog) && notInCatalog.GetBoolean()))
            {
                known.ValueKind.ShouldBe(JsonValueKind.Object, $"{id}: serial {serial} is not in the {tenant} catalog");
                known.GetProperty("modelCode").GetString().ShouldBe(product.GetProperty("modelCode").GetString(), $"{id}: model code of {serial}");
            }

            var hasOffset = scenario.TryGetProperty("purchaseDateOffsetMonths", out var offset) && offset.GetInt32() <= 0;
            var hasDate = scenario.TryGetProperty("purchaseDate", out _);
            (hasOffset ^ hasDate).ShouldBeTrue($"{id}: needs exactly one of purchaseDateOffsetMonths (<= 0) or purchaseDate");
            scenario.GetProperty("claim").GetProperty("purchase").TryGetProperty("date", out _).ShouldBeFalse($"{id}: purchase.date is computed");
        }
    }

    [Fact]
    public void Every_scenarios_evidence_files_exist()
    {
        foreach (var scenario in LoadScenarios())
        {
            var id = scenario.GetProperty("scenarioId").GetString()!;
            var evidence = scenario.GetProperty("evidence");
            if (evidence.TryGetProperty("invoice", out var invoice) && invoice.ValueKind == JsonValueKind.String)
            {
                File.Exists(Path.Combine(EvidenceRoot(), invoice.GetString()!)).ShouldBeTrue($"{id}: {invoice.GetString()} is missing");
            }

            var photos = evidence.GetProperty("photos").EnumerateArray().Select(p => p.GetString()!).ToList();
            photos.Count.ShouldBeInRange(0, 8, $"{id}: 1-8 photos (0 only for a missing-photo scenario)");
            photos.ShouldAllBe(photo => File.Exists(Path.Combine(EvidenceRoot(), photo)), $"{id}: a photo is missing");
        }
    }

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void Every_fixture_replays_and_its_structured_output_matches_its_schema(string scenarioId)
    {
        var folder = Path.Combine(RecordingsRoot(), scenarioId);
        if (!Directory.Exists(folder))
        {
            return; // a scenario that shares an earlier scenario's serial replays that scenario's fixtures
        }

        var scenario = Scenario(scenarioId);
        var invalidOutputs = StringList(scenario, "invalidOutputFixtures");
        var deniedTools = StringList(scenario, "deniedToolRequests");
        var fixtures = Fixtures(folder);
        fixtures.ShouldNotBeEmpty();
        invalidOutputs.ShouldAllBe(file => File.Exists(Path.Combine(folder, file)), $"{scenarioId}: a listed invalid-output fixture is missing");
        var requestedTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in fixtures.GroupBy(f => f.Agent))
        {
            group.Select(f => f.Index).Order().ShouldBe(Enumerable.Range(1, group.Count()), $"{scenarioId}: {group.Key} fixtures must be numbered 1..n");
        }

        foreach (var fixture in fixtures)
        {
            var text = ReplayFixtureVariables.Apply(File.ReadAllText(fixture.Path), SampleVariables);
            text.ShouldNotContain("{{", Case.Sensitive, $"{fixture.Name}: unknown placeholder");
            var recording = JsonSerializer.Deserialize<ReplayRecording>(text, ReplayRecording.JsonOptions).ShouldNotBeNull();
            var turn = recording.ToResult(ReplayModelProvider.ProviderName, "fixture-model");

            switch (turn.Stop)
            {
                case AiStopKind.Completed when turn.StructuredOutput is { } output:
                    var errors = Validator.Validate(SchemaByAgent[fixture.Agent], output).Errors;
                    if (invalidOutputs.Contains(Path.GetFileName(fixture.Path)))
                    {
                        errors.ShouldNotBeEmpty($"{fixture.Name} is listed in invalidOutputFixtures but matches {SchemaByAgent[fixture.Agent]}");
                    }
                    else
                    {
                        errors.ShouldBeEmpty($"{fixture.Name} does not match {SchemaByAgent[fixture.Agent]}: {string.Join("; ", errors)}");
                    }

                    break;

                case AiStopKind.ToolCalls:
                    turn.ToolCalls.ShouldNotBeEmpty($"{fixture.Name}: a ToolCalls turn needs tool calls");
                    turn.ToolCalls.ShouldAllBe(
                        call => ToolsByAgent[fixture.Agent].Contains(call.ToolName) || deniedTools.Contains(call.ToolName),
                        $"{fixture.Name}: tool not allowed for {fixture.Agent} and not listed in deniedToolRequests");
                    turn.ToolCalls.Select(c => c.CallId).ShouldBeUnique();
                    requestedTools.UnionWith(turn.ToolCalls.Select(c => c.ToolName));
                    break;

                case AiStopKind.Refused or AiStopKind.Truncated:
                    // A refusal or truncation carries only the model's text: no tool calls and no structured output.
                    turn.StructuredOutput.ShouldBeNull($"{fixture.Name}: a {turn.Stop} turn has no structured output");
                    turn.ToolCalls.ShouldBeEmpty($"{fixture.Name}: a {turn.Stop} turn has no tool calls");
                    break;

                default:
                    turn.Failure.ShouldNotBeNull($"{fixture.Name}: a turn that neither completes nor calls tools must be a recorded failure");
                    break;
            }
        }

        deniedTools.ShouldAllBe(tool => requestedTools.Contains(tool), $"{scenarioId}: every tool in deniedToolRequests is requested by a fixture");
    }

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void Fixtures_cite_only_the_scenarios_references_and_the_expected_clauses(string scenarioId)
    {
        var folder = Path.Combine(RecordingsRoot(), scenarioId);
        if (!Directory.Exists(folder))
        {
            return;
        }

        var scenario = Scenario(scenarioId);
        var invalidOutputs = StringList(scenario, "invalidOutputFixtures");
        var tenant = scenario.GetProperty("tenant").GetString()!;
        var references = scenario.GetProperty("references").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var clauseKeys = TenantClauseKeys(tenant);
        foreach (var (reference, target) in references.Where(r => r.Key.StartsWith("POL-", StringComparison.Ordinal)))
        {
            clauseKeys.ShouldContain(target, $"{scenarioId}: {reference} names {target}, which no {tenant} policy declares");
        }

        var photoCount = scenario.GetProperty("evidence").GetProperty("photos").GetArrayLength();
        var fixtures = Fixtures(folder);
        fixtures.Count(f => f.Agent == "evidence-photo").ShouldBeLessThanOrEqualTo(photoCount, $"{scenarioId}: one photo call per photo");

        var outputs = fixtures
            .Where(f => !invalidOutputs.Contains(Path.GetFileName(f.Path)))
            .Select(f => (f.Agent, f.Name, Turn: Replay(f.Path)))
            .ToList();
        foreach (var (_, name, turn) in outputs)
        {
            if (turn.StructuredOutput is not { } output)
            {
                continue;
            }

            foreach (var cited in ReferenceIds(output))
            {
                references.ShouldContainKey(cited, $"{name} cites {cited}, which the scenario does not declare");
            }
        }

        // Policy: the warranty_lookup tool is called once before the assessment.
        var policy = outputs.Where(o => o.Agent == "policy").ToList();
        if (policy.Any(o => o.Turn.StructuredOutput is not null))
        {
            policy.SelectMany(o => o.Turn.ToolCalls).Count(c => c.ToolName == "warranty_lookup").ShouldBe(1, $"{scenarioId}: one warranty_lookup call");
        }

        // Decision: the clauses it relies on are the scenario's labelled clause keys; the claimant text has no IDs.
        var decision = outputs.LastOrDefault(o => o.Agent == "decision" && o.Turn.StructuredOutput is not null);
        if (decision.Turn?.StructuredOutput is { } recommendation
            && scenario.GetProperty("expected").TryGetProperty("citedClauseKeys", out var expectedKeys))
        {
            var cited = recommendation.GetProperty("policyRefs").EnumerateArray()
                .Where(r => r.GetProperty("relevance").GetString() != "CONTEXT")
                .Select(r => references[r.GetProperty("ref").GetString()!])
                .Order(StringComparer.Ordinal);
            cited.ShouldBe(expectedKeys.EnumerateArray().Select(k => k.GetString()!).Order(StringComparer.Ordinal), $"{scenarioId}: cited clause keys");
            var screen = ClaimantTextScreen.Screen(recommendation.GetProperty("claimantExplanation").GetString()!);
            screen.IsSafe.ShouldBeTrue($"{scenarioId}: the claimant explanation contains '{screen.OffendingTerm}'");
        }
    }

    private static AiTurnResult Replay(string path)
    {
        var text = ReplayFixtureVariables.Apply(File.ReadAllText(path), SampleVariables);
        return JsonSerializer.Deserialize<ReplayRecording>(text, ReplayRecording.JsonOptions)!.ToResult(ReplayModelProvider.ProviderName, "fixture-model");
    }

    /// <summary>Every <c>EV-n</c>/<c>POL-n</c>/<c>GLB-n</c> in a reference-carrying property of an output.</summary>
    private static IEnumerable<string> ReferenceIds(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "ref" or "evidenceRef" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString()!;
                    }
                    else if (property.Name == "evidenceRefs" && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                yield return item.GetString()!;
                            }
                            else
                            {
                                foreach (var nested in ReferenceIds(item))
                                {
                                    yield return nested;
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var nested in ReferenceIds(property.Value))
                        {
                            yield return nested;
                        }
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in ReferenceIds(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static List<(string Path, string Name, string Agent, int Index)> Fixtures(string folder)
        => Directory.GetFiles(folder, "*.json")
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var match = FixtureName().Match(name);
                match.Success.ShouldBeTrue($"{name} is not '{{agent}}-{{n}}.json' with agent intake, evidence-invoice, evidence-photo, policy or decision");
                return (path, $"{Path.GetFileName(folder)}/{name}", match.Groups["agent"].Value, int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
            })
            .ToList();

    private static JsonElement Scenario(string scenarioId)
        => LoadScenarios().Single(s => s.GetProperty("scenarioId").GetString() == scenarioId);

    /// <summary>A scenario's optional string-array property (e.g. <c>invalidOutputFixtures</c>); empty when absent.</summary>
    private static HashSet<string> StringList(JsonElement scenario, string property)
        => scenario.TryGetProperty(property, out var values)
            ? values.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

    private static List<JsonElement> LoadScenarios()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ScenariosPath()));
        return document.RootElement.GetProperty("scenarios").EnumerateArray().Select(s => s.Clone()).ToList();
    }

    private static List<JsonElement> TenantSerials(string tenant)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "seed", "tenants", tenant, "serials.json")));
        return document.RootElement.EnumerateArray().Select(s => s.Clone()).ToList();
    }

    /// <summary>Clause keys declared in the front matter of the tenant's policy files.</summary>
    private static HashSet<string> TenantClauseKeys(string tenant)
        => Directory.GetFiles(Path.Combine(RepositoryRoot(), "seed", "tenants", tenant, "policies"), "*.md")
            .SelectMany(file => ClauseDeclaration().Matches(File.ReadAllText(file)).Select(m => m.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string ScenariosPath() => Path.Combine(RepositoryRoot(), "seed", "golden", "scenarios.json");

    private static string EvidenceRoot() => Path.Combine(RepositoryRoot(), "seed", "evidence");

    private static string RecordingsRoot() => Path.Combine(RepositoryRoot(), "tests", "fixtures", "ai-recordings");

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

    [GeneratedRegex(@"^(?<agent>intake|evidence-invoice|evidence-photo|policy|decision)-(?<n>[1-9][0-9]*)\.json$")]
    private static partial Regex FixtureName();

    [GeneratedRegex(@"^\s+(?<key>[A-Z][A-Z0-9]*-[A-Z0-9-]*?\d+(?:\.\d+)*):\s*\{\s*type:", RegexOptions.Multiline)]
    private static partial Regex ClauseDeclaration();
}
