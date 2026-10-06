using System.Text.Json;
using Warranty.Application.Actions;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// The Consequential entries of the tool catalog (contracts/agents-and-tools.md): <c>create_repair_request</c>
/// and <c>notify_customer</c>. They are registered so that the registry knows them as Consequential — an agent
/// asking for one is denied as a <c>TOOL_SCOPE_VIOLATION</c> rather than as an unknown tool — and they are
/// never offered to any model (<see cref="ToolRegistry.For"/> returns ReadOnly tools only).
/// <para>
/// They are descriptor-only and never execute: consequential effects happen only in the
/// <see cref="ActionExecutor"/>, which calls the simulated integration ports directly and only for a
/// guardrail-issued <c>ApprovedAction</c> or a recorded reviewer decision (constitution II). A tool call,
/// even with the action executor as caller, is therefore answered with an error and changes nothing.
/// </para>
/// </summary>
public sealed class ConsequentialTool : ITool
{
    private ConsequentialTool(ToolDescriptor descriptor) => Descriptor = descriptor;

    public ToolDescriptor Descriptor { get; }

    /// <summary><c>create_repair_request</c> <c>{ claimId, serviceCenterId }</c>: a simulated repair request at a service center.</summary>
    public static ConsequentialTool CreateRepairRequest() => new(new ToolDescriptor(
        ToolNames.CreateRepairRequest,
        "Creates a simulated repair request for an approved claim at a service center. Executed only by the action executor.",
        ToolSupport.Schema("""
            {"type":"object","additionalProperties":false,
             "properties":{"claimId":{"type":"string","format":"uuid"},"serviceCenterId":{"type":"string","format":"uuid"}},
             "required":["claimId","serviceCenterId"]}
            """),
        ToolSideEffect.Consequential,
        ToolSupport.Callers(ToolDescriptor.ActionExecutorCaller)));

    /// <summary><c>notify_customer</c> <c>{ claimId, template }</c>: a simulated outbox entry naming a notification template.</summary>
    public static ConsequentialTool NotifyCustomer() => new(new ToolDescriptor(
        ToolNames.NotifyCustomer,
        "Queues a simulated customer notification for a finalized claim. Executed only by the action executor.",
        ToolSupport.Schema($$$"""
            {"type":"object","additionalProperties":false,
             "properties":{"claimId":{"type":"string","format":"uuid"},
                           "template":{"type":"string","enum":["{{{ActionExecutor.ApprovedTemplate}}}","{{{ActionExecutor.RejectedTemplate}}}"]}},
             "required":["claimId","template"]}
            """),
        ToolSideEffect.Consequential,
        ToolSupport.Callers(ToolDescriptor.ActionExecutorCaller)));

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
        => Task.FromResult(ToolResult.Error(
            $"Tool '{Descriptor.Name}' is executed only by the action executor for a guardrail-approved action, not through tool calls."));
}
