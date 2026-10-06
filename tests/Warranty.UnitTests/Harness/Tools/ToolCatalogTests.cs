using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.AI.Harness;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.UnitTests.Infrastructure;
using static Warranty.UnitTests.Harness.Tools.ToolTestKit;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary>The read-only tool catalog as a whole (contracts/agents-and-tools.md "Tool catalog").</summary>
public sealed class ToolCatalogTests
{
    private static readonly ITool[] Tools =
    [
        new CustomerLookupTool(Substitute.For<IClaimRepository>(), Substitute.For<ICrmClient>()),
        new ProductLookupTool(Substitute.For<ICatalogRepository>()),
        new WarrantyLookupTool(Substitute.For<IClaimRepository>(), Substitute.For<ICatalogRepository>(), Substitute.For<IPolicyRepository>()),
        new InvoiceValidationTool(Substitute.For<IClaimRepository>()),
        new ClaimHistoryLookupTool(Substitute.For<IClaimRepository>()),
        new SearchPolicyKnowledgeTool(Substitute.For<IKnowledgeRetriever>(), Substitute.For<IClaimRepository>(), Substitute.For<ICatalogRepository>()),
        new SearchGlobalKnowledgeTool(Substitute.For<IKnowledgeRetriever>()),
        ConsequentialTool.CreateRepairRequest(),
        ConsequentialTool.NotifyCustomer(),
    ];

    private static readonly string[] ConsequentialToolNames = ["create_repair_request", "notify_customer"];

    [Theory]
    [InlineData(AgentNames.Intake, new[] { "customer_lookup", "product_lookup" })]
    [InlineData(AgentNames.Evidence, new[] { "invoice_validation", "product_lookup" })]
    [InlineData(AgentNames.Policy, new[] { "search_global_knowledge", "search_policy_knowledge", "warranty_lookup" })]
    [InlineData(AgentNames.Decision, new[] { "claim_history_lookup", "search_global_knowledge" })]
    [InlineData(AgentNames.Risk, new[] { "claim_history_lookup" })]
    [InlineData(ToolDescriptor.ActionExecutorCaller, new string[0])]
    public void Each_agent_is_offered_exactly_its_tools(string agent, string[] tools)
        => new ToolRegistry(Tools).For(agent).Select(t => t.Descriptor.Name).ShouldBe(tools);

    [Fact]
    public void Every_tool_has_a_strict_schema_without_tenant_fields_and_only_the_action_executor_may_call_consequential_ones()
    {
        foreach (var tool in Tools)
        {
            var consequential = ConsequentialToolNames.Contains(tool.Descriptor.Name);
            tool.Descriptor.SideEffect.ShouldBe(consequential ? ToolSideEffect.Consequential : ToolSideEffect.ReadOnly, tool.Descriptor.Name);
            if (consequential)
            {
                tool.Descriptor.AllowedCallers.ShouldBe([ToolDescriptor.ActionExecutorCaller], ignoreOrder: false, tool.Descriptor.Name);
            }

            tool.Descriptor.Problems().ShouldBeEmpty(tool.Descriptor.Name);
            AllPropertyNames(tool.Descriptor.InputSchema)
                .ShouldAllBe(p => !p.Contains("tenant", StringComparison.OrdinalIgnoreCase), tool.Descriptor.Name);
        }
    }

    [Theory]
    [InlineData("customer_lookup", AgentNames.Intake, """{"tenantId":"0199a000-0000-7000-8000-000000000002"}""")]
    [InlineData("claim_history_lookup", AgentNames.Decision, """{"tenantId":"0199a000-0000-7000-8000-000000000002"}""")]
    [InlineData("product_lookup", AgentNames.Intake, """{"modelCode":"AUR-TAB10","serialNumber":"SN-1","tenant":"borealis"}""")]
    [InlineData("warranty_lookup", AgentNames.Policy, """{"component":"BATTERY","tenantSlug":"borealis"}""")]
    [InlineData("warranty_lookup", AgentNames.Policy, """{"component":"WHEEL"}""")]
    [InlineData("search_global_knowledge", AgentNames.Decision, """{"query":"terms","documentType":"WarrantyPolicy"}""")]
    [InlineData("search_policy_knowledge", AgentNames.Policy, """{"query":"terms","namespace":"tenant-borealis"}""")]
    [InlineData("invoice_validation", AgentNames.Evidence,
        """{"invoiceRef":"EV-1","extracted":{"invoiceDate":"UNKNOWN","modelCode":"UNKNOWN","serial":"UNKNOWN","amount":0,"seller":"UNKNOWN","tenantId":"x"}}""")]
    public async Task Extra_or_out_of_range_arguments_are_rejected_before_the_tool_runs(string tool, string caller, string arguments)
    {
        var references = new ReferenceRegistry();
        references.IssueEvidence(Guid.NewGuid());
        var run = new AdjudicationContext(
            RunId, new FakeTenantContext(Aurora),
            new CaseContext(
                ClaimId, 1, "ABCDEFGHJK", ClaimChannel.ClaimantPortal, new DateOnly(2026, 9, 30), PurchaseDate, Seller, 449m, "EUR",
                Region.EU, ModelCode, Serial, "Battery drains.", null, new CaseCustomerView("NO", Region.EU), [],
                ClaimHistoryCounts.None, false, 0),
            references);
        var invoker = new ToolInvoker(
            new ToolRegistry(Tools), Substitute.For<IAiOpsRepository>(), Substitute.For<ISecurityEventWriter>(), new NoRedaction(), new FakeTimeProvider());

        var result = await invoker.InvokeAsync(caller, run, new AiToolCall("call-1", tool, Args(arguments)), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.Result.GetProperty("error").GetString()!.ShouldStartWith("Invalid arguments:");
    }

    [Fact]
    public async Task The_harness_registers_every_tool_and_the_risk_capability_can_resolve_the_history_lookup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Aurora));
        foreach (var port in new[]
                 {
                     typeof(IClaimRepository), typeof(ICatalogRepository), typeof(IPolicyRepository), typeof(ICrmClient),
                     typeof(IKnowledgeRetriever), typeof(IAiOpsRepository), typeof(ISecurityEventWriter), typeof(IPiiRedactor),
                 })
        {
            services.AddScoped(port, _ => Substitute.For([port], []));
        }

        services.AddWarrantyAiHarness();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ToolRegistry>().All.Select(t => t.Descriptor.Name).ShouldBe(
            [
                "customer_lookup", "product_lookup", "warranty_lookup", "invoice_validation", "claim_history_lookup", "search_policy_knowledge",
                "search_global_knowledge", "create_repair_request", "notify_customer",
            ],
            ignoreOrder: true);
        scope.ServiceProvider.GetRequiredService<ClaimHistoryLookupTool>()
            .ShouldBeSameAs(scope.ServiceProvider.GetServices<ITool>().OfType<ClaimHistoryLookupTool>().Single());
    }

    private sealed class NoRedaction : IPiiRedactor
    {
        public RedactionResult Redact(string text) => new(text, 0);
    }
}
