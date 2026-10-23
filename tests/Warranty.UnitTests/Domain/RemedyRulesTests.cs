using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.MigrationService.Seeding;

namespace Warranty.UnitTests.Domain;

/// <summary>
/// <see cref="RemedyRules.Validate"/> (data-model.md <c>remedy_rules</c>) checked case by case against the
/// remedy rules schema the MigrationService validates seed files with, so the two cannot drift apart.
/// </summary>
public sealed class RemedyRulesTests
{
    [Fact]
    public void The_schema_examples_are_valid_for_the_schema_and_the_domain()
    {
        var examples = RemedyRulesContract.Schema.Examples;
        examples.Count.ShouldBe(2);

        foreach (var example in examples)
        {
            RemedyRulesContract.SchemaErrors(example).ShouldBeEmpty();
            Should.NotThrow(() => RemedyRulesContract.Deserialize(example).Validate());
        }
    }

    [Fact]
    public void The_aurora_and_borealis_examples_read_as_the_fixed_remedy_rules()
    {
        var examples = RemedyRulesContract.Schema.Examples;

        RemedyRulesContract.ShouldBeSameRules(RemedyRulesContract.Deserialize(examples[0]), RemedyRulesContract.Aurora);
        RemedyRulesContract.ShouldBeSameRules(RemedyRulesContract.Deserialize(examples[1]), RemedyRulesContract.Borealis);
    }

    [Fact]
    public void A_version_without_any_row_is_valid_and_gives_no_remedy()
    {
        var rules = new RemedyRules(null, null, null, null);

        Should.NotThrow(rules.Validate);
        RemedyRulesContract.SchemaErrors(JsonSerializer.SerializeToElement(new { })).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{ "coveredDefect": { "remedy": "Repair", "allowed": ["Refund", "Replacement"] } }""", "remedy and allowed together")]
    [InlineData("""{ "coveredDefect": { "allowed": ["Refund"] } }""", "one-item allowed list")]
    [InlineData("""{ "coveredDefect": { "allowed": ["Refund", "Refund"] } }""", "the same remedy twice")]
    [InlineData("""{ "coveredDefect": { } }""", "a row with neither remedy nor allowed")]
    [InlineData("""{ "repairNotEconomical": { "limitPercentOfClaimValue": 0, "allowed": ["Refund", "Replacement"] } }""", "limit 0")]
    [InlineData("""{ "repairNotEconomical": { "limitPercentOfClaimValue": 101, "allowed": ["Refund", "Replacement"] } }""", "limit 101")]
    [InlineData("""{ "repairFailed": { "afterFailedAttempts": 0, "remedy": "Refund" } }""", "attempts 0")]
    [InlineData("""{ "repairFailed": { "afterFailedAttempts": 6, "remedy": "Refund" } }""", "attempts 6")]
    [InlineData("""{ "protectionPlan": { "liftsAccidentalDamageExclusion": true } }""", "plan lifting the exclusion without a remedy")]
    [InlineData("""{ "protectionPlan": { "liftsAccidentalDamageExclusion": false, "remedy": "Repair" } }""", "plan not lifting the exclusion with a remedy")]
    public void Inconsistent_rows_are_rejected_by_the_schema_and_the_domain(string json, string reason)
    {
        var element = JsonDocument.Parse(json).RootElement;

        RemedyRulesContract.SchemaErrors(element).ShouldNotBeEmpty(reason);
        Should.Throw<ArgumentException>(() => RemedyRulesContract.Deserialize(element).Validate(), reason);
    }

    [Theory]
    [InlineData("""{ "repairNotEconomical": { "limitPercentOfClaimValue": 1, "remedy": "Refund" } }""")]
    [InlineData("""{ "repairNotEconomical": { "limitPercentOfClaimValue": 100, "remedy": "Refund" } }""")]
    [InlineData("""{ "repairFailed": { "afterFailedAttempts": 1, "remedy": "Replacement" } }""")]
    [InlineData("""{ "repairFailed": { "afterFailedAttempts": 5, "allowed": ["Refund", "Replacement", "Repair"] } }""")]
    [InlineData("""{ "protectionPlan": { "liftsAccidentalDamageExclusion": true, "allowed": ["Repair", "Replacement"] } }""")]
    public void Boundary_values_are_accepted_by_the_schema_and_the_domain(string json)
    {
        var element = JsonDocument.Parse(json).RootElement;

        RemedyRulesContract.SchemaErrors(element).ShouldBeEmpty();
        Should.NotThrow(() => RemedyRulesContract.Deserialize(element).Validate());
    }

    [Fact]
    public void A_single_remedy_is_set_automatically_and_an_allowed_list_needs_a_reviewer()
    {
        var single = new RemedyChoice(RemedyType.Repair, null);
        single.NeedsReviewer.ShouldBeFalse();
        single.AllowedRemedies.ShouldBe([RemedyType.Repair]);

        var choice = new RemedyChoice(null, [RemedyType.Refund, RemedyType.Replacement]);
        choice.NeedsReviewer.ShouldBeTrue();
        choice.AllowedRemedies.ShouldBe([RemedyType.Refund, RemedyType.Replacement]);
    }

    [Fact]
    public void A_plan_that_does_not_lift_the_exclusion_permits_no_remedy()
    {
        var plan = RemedyRulesContract.Aurora.ProtectionPlan!;

        plan.LiftsAccidentalDamageExclusion.ShouldBeFalse();
        plan.AllowedRemedies.ShouldBeEmpty();
        plan.NeedsReviewer.ShouldBeFalse();
    }
}

/// <summary>
/// The remedy rules contract shared by the domain and seed tests: the schema embedded in the
/// MigrationService (T202) and the fixed rule sets of tasks.md "Fixed identifiers".
/// </summary>
internal static class RemedyRulesContract
{
    private const string ResourceName = "Warranty.MigrationService.Seeding.Schemas.remedy-rules.schema.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Aurora (AUR-WP v1 and v2).</summary>
    public static readonly RemedyRules Aurora = new(
        CoveredDefect: new RemedyChoice(RemedyType.Repair, null),
        RepairNotEconomical: null,
        RepairFailed: new RepairFailedRule(2, RemedyType.Replacement, null),
        ProtectionPlan: new ProtectionPlanRule(false, null, null));

    /// <summary>Borealis (BOR-WP v1).</summary>
    public static readonly RemedyRules Borealis = new(
        CoveredDefect: new RemedyChoice(RemedyType.Repair, null),
        RepairNotEconomical: new RepairNotEconomicalRule(60, null, [RemedyType.Refund, RemedyType.Replacement]),
        RepairFailed: new RepairFailedRule(2, RemedyType.Refund, null),
        ProtectionPlan: new ProtectionPlanRule(true, RemedyType.Repair, null));

    public static (JsonSchema Schema, IReadOnlyList<JsonElement> Examples) Schema { get; } = Load();

    /// <summary>Compares two rule sets by their JSON form, so the concrete list types do not matter.</summary>
    public static void ShouldBeSameRules(RemedyRules actual, RemedyRules expected)
        => JsonSerializer.Serialize(actual, Json).ShouldBe(JsonSerializer.Serialize(expected, Json));

    public static RemedyRules Deserialize(JsonElement element)
        => element.Deserialize<RemedyRules>(Json) ?? throw new InvalidDataException("The remedy rules are null.");

    public static IReadOnlyList<string> SchemaErrors(JsonElement element)
    {
        var evaluation = Schema.Schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return evaluation.IsValid
            ? []
            : (evaluation.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .DefaultIfEmpty("invalid")
                .ToList();
    }

    private static (JsonSchema, IReadOnlyList<JsonElement>) Load()
    {
        using var stream = typeof(SeedFiles).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        var root = JsonDocument.Parse(stream).RootElement.Clone();
        var schema = JsonSchema.Build(root, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var examples = root.GetProperty("examples").EnumerateArray().Select(e => e.Clone()).ToList();
        return (schema, examples);
    }
}
