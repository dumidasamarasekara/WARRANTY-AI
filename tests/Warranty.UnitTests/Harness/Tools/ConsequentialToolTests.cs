using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.UnitTests.Infrastructure;
using static Warranty.UnitTests.Harness.Tools.ToolTestKit;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary>
/// The Consequential tools of the catalog (contracts/agents-and-tools.md, enforcement rules 1–2; scenario
/// <c>US4-consequential-tool</c>): never offered to a model, and an agent asking for one is denied with a
/// <c>TOOL_SCOPE_VIOLATION</c> security event. They never execute through a tool call.
/// </summary>
public sealed class ConsequentialToolTests
{
    private static readonly string[] Agents = [AgentNames.Intake, AgentNames.Evidence, AgentNames.Policy, AgentNames.Decision, AgentNames.Risk];

    private readonly IAiOpsRepository _aiOps = Substitute.For<IAiOpsRepository>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly List<ToolCall> _toolCalls = [];

    public ConsequentialToolTests() => _aiOps.When(a => a.AddToolCall(Arg.Any<ToolCall>())).Do(c => _toolCalls.Add(c.Arg<ToolCall>()));

    public static TheoryData<string, string> AgentRequests()
    {
        var data = new TheoryData<string, string>();
        foreach (var agent in Agents)
        {
            data.Add(agent, ToolNames.CreateRepairRequest);
            data.Add(agent, ToolNames.NotifyCustomer);
        }

        return data;
    }

    [Theory]
    [InlineData(ToolNames.CreateRepairRequest, """{"claimId":"0199a000-0000-7000-8000-0000000000c1","serviceCenterId":"0199a000-0000-7000-8000-0000000000f1"}""")]
    [InlineData(ToolNames.NotifyCustomer, """{"claimId":"0199a000-0000-7000-8000-0000000000c1","template":"claim-approved"}""")]
    public void The_consequential_tools_match_the_contract(string name, string validArguments)
    {
        var tool = Registry().Find(name).ShouldNotBeNull();

        tool.Descriptor.SideEffect.ShouldBe(ToolSideEffect.Consequential);
        tool.Descriptor.AllowedCallers.ShouldBe([ToolDescriptor.ActionExecutorCaller]);
        tool.Descriptor.Problems().ShouldBeEmpty();
        AllPropertyNames(tool.Descriptor.InputSchema).ShouldBe(
            name == ToolNames.CreateRepairRequest ? ["claimId", "serviceCenterId"] : ["claimId", "template"], ignoreOrder: true);
        Args(validArguments).EnumerateObject().Select(p => p.Name).ShouldBe(AllPropertyNames(tool.Descriptor.InputSchema), ignoreOrder: true);
    }

    [Fact]
    public void No_agent_and_not_the_action_executor_is_offered_a_consequential_tool()
    {
        var registry = Registry();
        var invoker = Invoker(registry);

        foreach (var caller in Agents.Append(ToolDescriptor.ActionExecutorCaller))
        {
            registry.For(caller).ShouldAllBe(t => t.Descriptor.SideEffect == ToolSideEffect.ReadOnly, caller);
            var offered = invoker.For(caller, Run()).Definitions.Select(d => d.Name).ToList();
            offered.ShouldNotContain(ToolNames.CreateRepairRequest, caller);
            offered.ShouldNotContain(ToolNames.NotifyCustomer, caller);
        }
    }

    [Theory]
    [MemberData(nameof(AgentRequests))]
    public async Task An_agent_requesting_a_consequential_tool_is_denied_with_one_scope_violation(string agent, string tool)
    {
        var run = Run();

        var result = await Invoker(Registry()).For(agent, run).InvokeAsync(
            new AiToolCall("call-1", tool, Args("""{"claimId":"0199a000-0000-7000-8000-0000000000c1","serviceCenterId":"x","template":"claim-approved"}""")),
            TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.Result.GetProperty("error").GetString().ShouldBe($"Tool '{tool}' is not available.");
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.ToolScopeViolation, agent, $"tool:{tool}", Arg.Any<object>(), Arg.Any<CancellationToken>());
        _securityEvents.ReceivedCalls().Count().ShouldBe(1);

        var row = _toolCalls.ShouldHaveSingleItem();
        (row.TenantId, row.RunId, row.Agent, row.Tool).ShouldBe((Aurora, (Guid?)RunId, agent, tool));
        (row.Allowed, row.DenialReason, row.Status).ShouldBe((false, "consequential_tool", "denied"));
    }

    [Theory]
    [InlineData(ToolNames.CreateRepairRequest, """{"claimId":"0199a000-0000-7000-8000-0000000000c1","serviceCenterId":"0199a000-0000-7000-8000-0000000000f1"}""")]
    [InlineData(ToolNames.NotifyCustomer, """{"claimId":"0199a000-0000-7000-8000-0000000000c1","template":"claim-rejected"}""")]
    public async Task A_tool_call_never_executes_a_consequential_action_even_for_the_action_executor(string tool, string arguments)
    {
        var result = await Invoker(Registry()).InvokeAsync(
            ToolDescriptor.ActionExecutorCaller, Run(), new AiToolCall("call-1", tool, Args(arguments)), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.Result.GetProperty("error").GetString()!.ShouldContain("executed only by the action executor");
        _securityEvents.ReceivedCalls().ShouldBeEmpty();
        (_toolCalls.Single().Allowed, _toolCalls.Single().Status).ShouldBe((true, "error"));
    }

    private static ToolRegistry Registry() => new([ConsequentialTool.CreateRepairRequest(), ConsequentialTool.NotifyCustomer()]);

    private ToolInvoker Invoker(ToolRegistry registry) => new(registry, _aiOps, _securityEvents, new NoRedaction(), new FakeTimeProvider());

    private static AdjudicationContext Run() => new(
        RunId, new FakeTenantContext(Aurora),
        new CaseContext(
            ClaimId, 1, "ABCDEFGHJK", ClaimChannel.ClaimantPortal, new DateOnly(2026, 9, 30), PurchaseDate, Seller, 449m, "EUR",
            Region.EU, ModelCode, Serial, "Battery drains.", null, new CaseCustomerView("NO", Region.EU), [],
            ClaimHistoryCounts.None, false, 0),
        new ReferenceRegistry());

    private sealed class NoRedaction : IPiiRedactor
    {
        public RedactionResult Redact(string text) => new(text, 0);
    }
}
