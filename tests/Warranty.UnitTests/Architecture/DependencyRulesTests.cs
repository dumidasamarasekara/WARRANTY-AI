using System.Reflection;
using NetArchTest.Rules;

namespace Warranty.UnitTests.Architecture;

/// <summary>
/// The module boundaries of plan.md: the guardrails and the domain stay free of AI code, only the
/// gateway talks to an AI vendor SDK, and consequential actions need a guardrail-issued
/// <c>ApprovedAction</c> and run only in the <c>ActionExecutor</c>.
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

    /// <summary>The simulated adapter implements the port (and registers it), so it is the one assembly left out.</summary>
    public static TheoryData<string> AssembliesOtherThanTheRefundAdapter =>
        new(ProductionAssemblies.Where(name => name != Simulated));

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
    /// Refunds are a consequential action (constitution II, research R10): only the <c>ActionExecutor</c>
    /// (with its compiler-generated nested types) may depend on <c>IRefundService</c>; the port itself is
    /// the only other type allowed to mention it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AssembliesOtherThanTheRefundAdapter))]
    public void Only_the_ActionExecutor_depends_on_the_refund_service(string assembly)
    {
        const string refundService = "Warranty.Application.Abstractions.Integrations.IRefundService";
        const string executor = "Warranty.Application.Actions.ActionExecutor";

        var result = Types.InAssembly(Assembly.Load(assembly)).ShouldNot().HaveDependencyOn(refundService).GetResult();

        (result.FailingTypeNames ?? [])
            .Where(name => name != refundService && name != executor && !name.StartsWith(executor + "+", StringComparison.Ordinal)
                && !name.StartsWith(executor + "/", StringComparison.Ordinal))
            .ShouldBeEmpty("only the ActionExecutor may request a refund");
    }

    /// <summary>Positive control: the rule does see a dependency on the refund port.</summary>
    [Fact]
    public void The_refund_rule_sees_the_simulated_refund_adapter() =>
        Types.InAssembly(Assembly.Load(Simulated)).That()
            .HaveDependencyOn("Warranty.Application.Abstractions.Integrations.IRefundService")
            .GetTypes().Select(type => type.Name).ShouldContain("SimulatedRefundService");

    /// <summary>
    /// The remedy evaluators of 002 and 003 (<c>RemedyPolicyEvaluator</c>, <c>ReplacementPolicy</c>) are deterministic
    /// and reference no AI project (003 plan.md, constitution II); the assembly rule above covers the rest of the guardrails.
    /// </summary>
    [Fact]
    public void Guardrail_remedy_rules_reference_no_AI_project()
    {
        const string remedies = "Warranty.Guardrails.Remedies";
        var types = Types.InAssembly(Assembly.Load(Guardrails)).That().ResideInNamespace(remedies);

        types.GetTypes().Select(type => type.Name).ShouldContain("RemedyPolicyEvaluator");
        var result = types.ShouldNot().HaveDependencyOnAny([Harness, Gateway, "Warranty.AI", Knowledge, Infrastructure, .. AiSdks]).GetResult();
        (result.FailingTypeNames ?? []).ShouldBeEmpty($"{remedies} must stay free of AI code");
    }

    [Fact]
    public void The_replacement_policy_lives_with_the_remedy_rules_in_the_guardrails()
    {
        var policy = Assembly.Load(Guardrails).GetTypes().SingleOrDefault(type => type.Name == "ReplacementPolicy");
        Assert.SkipWhen(policy is null, "ReplacementPolicy is introduced by T323.");

        policy!.Namespace.ShouldBe("Warranty.Guardrails.Remedies");
    }

    /// <summary>
    /// A replacement is created on approval or a remedy decision and cancelled on a change away from Replacement,
    /// both consequential (003 research R3, R13): only the <c>ActionExecutor</c> (with its compiler-generated nested
    /// types) calls <c>Replacement.Create</c> or a <c>Cancel…</c> method. The MigrationService seeders may create the
    /// demonstration history through the same domain methods (as 002's <c>RemedyScenarioSeeder</c> does for repairs).
    /// </summary>
    [Fact]
    public void Only_the_ActionExecutor_creates_or_cancels_a_replacement()
    {
        const string executor = "Warranty.Application.Actions.ActionExecutor";

        var callers = ProductionAssemblies
            .Where(name => name != Domain)
            .SelectMany(name => ReplacementLifecycleCallers(Assembly.Load(name)))
            .Where(caller => caller.Type != executor && !caller.Type.StartsWith("Warranty.MigrationService.Seeding.", StringComparison.Ordinal))
            .Select(caller => $"{caller.Type} calls Replacement.{caller.Method}")
            .ToList();

        callers.ShouldBeEmpty("only the ActionExecutor may create or cancel a replacement");
    }

    /// <summary>Positive control: the scan does see a call to <c>Replacement.Create</c> (the domain tests make some).</summary>
    [Fact]
    public void The_replacement_rule_sees_calls_to_Replacement_Create() =>
        ReplacementLifecycleCallers(typeof(DependencyRulesTests).Assembly).Select(caller => caller.Method).ShouldContain("Create");

    /// <summary>
    /// The calls to <c>Replacement.Create</c> and to any <c>Replacement.Cancel…</c> method in the method bodies of an
    /// assembly, read from the IL with Mono.Cecil (NetArchTest only sees type dependencies). A caller is named by its
    /// top-level type, so lambdas and async state machines count as the type that declares them.
    /// </summary>
    private static IEnumerable<(string Type, string Method)> ReplacementLifecycleCallers(Assembly assembly)
    {
        const string replacement = "Warranty.Domain.Fulfillment.Replacement";

        using var module = Mono.Cecil.ModuleDefinition.ReadModule(assembly.Location);
        return module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody).Select(method => (Type: type, Method: method)))
            .SelectMany(caller => caller.Method.Body.Instructions
                .Select(instruction => instruction.Operand)
                .OfType<Mono.Cecil.MethodReference>()
                .Where(called => called.DeclaringType.FullName == replacement
                    && (called.Name == "Create" || called.Name.StartsWith("Cancel", StringComparison.Ordinal)))
                .Select(called => (TopLevel(caller.Type).FullName, called.Name)))
            .Distinct()
            .ToList();

        static Mono.Cecil.TypeDefinition TopLevel(Mono.Cecil.TypeDefinition type) =>
            type.DeclaringType is { } outer ? TopLevel(outer) : type;
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
