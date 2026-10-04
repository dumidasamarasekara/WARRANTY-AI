using System.Reflection;
using NetArchTest.Rules;

namespace Warranty.UnitTests.Architecture;

/// <summary>
/// The module boundaries of plan.md: the guardrails and the domain stay free of AI code, only the
/// gateway talks to an AI vendor SDK, and consequential actions need a guardrail-issued
/// <c>ApprovedAction</c>.
/// </summary>
public sealed class DependencyRulesTests
{
    private const string Domain = "Warranty.Domain";
    private const string Application = "Warranty.Application";
    private const string Guardrails = "Warranty.Guardrails";
    private const string Harness = "Warranty.AI.Harness";
    private const string Gateway = "Warranty.AI.Gateway";
    private const string Knowledge = "Warranty.Knowledge";
    private const string Infrastructure = "Warranty.Infrastructure";
    private const string Simulated = "Warranty.Integrations.Simulated";
    private const string Api = "Warranty.Api";
    private const string MigrationService = "Warranty.MigrationService";
    private const string ServiceDefaults = "Warranty.ServiceDefaults";

    private static readonly string[] ProductionAssemblies =
    [
        Domain, Application, Guardrails, Harness, Gateway, Knowledge, Infrastructure, Simulated, Api, MigrationService,
        ServiceDefaults,
    ];

    /// <summary>Vendor AI SDKs and the AI abstraction layer.</summary>
    private static readonly string[] AiSdks = ["Anthropic", "OllamaSharp", "Microsoft.Extensions.AI"];

    public static TheoryData<string> AiFreeAssemblies => new(Domain, Guardrails);

    public static TheoryData<string> AssembliesOtherThanGateway =>
        new(ProductionAssemblies.Where(name => name != Gateway));

    public static TheoryData<string> AssembliesThatMayNotUseSimulatedIntegrations =>
        new(ProductionAssemblies.Where(name => name is not (Simulated or Api or MigrationService)));

    [Theory]
    [MemberData(nameof(AiFreeAssemblies))]
    public void Domain_and_guardrails_reference_no_AI_harness_gateway_knowledge_or_infrastructure_code(string assembly) =>
        ShouldNotDependOn(assembly, [Harness, Gateway, "Warranty.AI", Knowledge, Infrastructure, .. AiSdks]);

    [Theory]
    [MemberData(nameof(AssembliesOtherThanGateway))]
    public void Only_the_gateway_references_a_vendor_AI_SDK(string assembly) =>
        ShouldNotDependOn(assembly, ["Anthropic", "OllamaSharp"]);

    /// <summary>Positive control: both detection methods do see a real dependency.</summary>
    [Fact]
    public void The_gateway_is_where_the_Anthropic_SDK_is_used()
    {
        var gateway = Assembly.Load(Gateway);
        gateway.GetReferencedAssemblies().Select(reference => reference.Name).ShouldContain("Anthropic");
        Types.InAssembly(gateway).That().HaveDependencyOn("Anthropic").GetTypes().ShouldNotBeEmpty();
    }

    [Fact]
    public void The_harness_does_not_reference_the_gateway() =>
        ShouldNotDependOn(Harness, [Gateway, .. AiSdks]);

    [Fact]
    public void Application_does_not_reference_EF_Core_or_Npgsql() =>
        ShouldNotDependOn(Application, ["Microsoft.EntityFrameworkCore", "Npgsql", "Pgvector"]);

    [Theory]
    [MemberData(nameof(AssembliesThatMayNotUseSimulatedIntegrations))]
    public void Simulated_integrations_are_referenced_only_by_the_api_and_the_migration_service(string assembly) =>
        ShouldNotDependOn(assembly, [Simulated]);

    [Fact]
    public void ApprovedAction_can_only_be_issued_by_the_guardrails()
    {
        var approvedAction = ProductionAssemblies
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes())
            .SingleOrDefault(type => type.Name == "ApprovedAction");
        Assert.SkipWhen(approvedAction is null, "ApprovedAction is introduced by the guardrail engine (T066).");

        approvedAction!.Assembly.GetName().Name.ShouldBe(Guardrails);
        approvedAction.GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
        approvedAction.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotContain(constructor => constructor.IsFamily || constructor.IsFamilyOrAssembly,
                "a protected constructor would let a subclass outside the guardrails create one");
    }

    /// <summary>
    /// Checks both the types (NetArchTest, by namespace prefix) and the compiled assembly references,
    /// so neither a stray <c>using</c> nor an unused project reference slips through.
    /// </summary>
    private static void ShouldNotDependOn(string assemblyName, string[] forbidden)
    {
        var assembly = Assembly.Load(assemblyName);

        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        (result.FailingTypeNames ?? []).ShouldBeEmpty(
            $"{assemblyName} types must not depend on {string.Join(", ", forbidden)}");

        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? "");
        references
            .Where(reference => forbidden.Any(name => reference == name || reference.StartsWith(name + ".", StringComparison.Ordinal)))
            .ShouldBeEmpty($"{assemblyName} must not reference {string.Join(", ", forbidden)}");
    }
}
