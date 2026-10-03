using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Warranty.AI.Harness.Context;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;
using Warranty.Domain.Common;

namespace Warranty.AI.Harness.Tools;

/// <summary>
/// Executes tool calls (contracts/agents-and-tools.md, enforcement rules 2–5). Every invocation
/// re-checks the caller against the tool's allowed callers, validates the arguments against the strict
/// input schema, checks reference arguments against the run's <see cref="ReferenceRegistry"/> and
/// injects a <see cref="ToolInvocationContext"/> with the run's tenant context. Unknown, disallowed,
/// invalid or failing calls become error results for the model; a Consequential tool requested by an
/// agent also writes a <c>TOOL_SCOPE_VIOLATION</c> security event. Each call — allowed or not — adds
/// one <c>aiops.tool_calls</c> row with redacted arguments to the caller's unit of work.
/// </summary>
public sealed class ToolInvoker(
    ToolRegistry registry,
    IAiOpsRepository aiOps,
    ISecurityEventWriter securityEvents,
    IPiiRedactor redactor,
    TimeProvider time)
{
    public const int MaxRecordedArgumentsLength = 4_000;

    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    /// <summary>An invoker scoped to one caller of one run, offering only that caller's ReadOnly tools.</summary>
    public IToolInvoker For(string caller, AdjudicationContext run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentNullException.ThrowIfNull(run);
        return new ScopedInvoker(this, caller, run, registry.DefinitionsFor(caller));
    }

    public async Task<AiToolResult> InvokeAsync(string caller, AdjudicationContext run, AiToolCall call, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(call);
        var startedAt = time.GetUtcNow();
        var start = time.GetTimestamp();

        var outcome = await ExecuteAsync(caller, run, call, ct);

        aiOps.AddToolCall(new ToolCall
        {
            Id = Guid.CreateVersion7(),
            TenantId = run.Tenant.TenantId,
            RunId = run.RunId,
            Agent = caller,
            Tool = call.ToolName,
            ArgumentsJson = RedactedArguments(call.Arguments),
            Allowed = outcome.DenialReason is null,
            DenialReason = outcome.DenialReason,
            ResultSummary = redactor.Redact(outcome.Result.Summary).Text,
            LatencyMs = (long)time.GetElapsedTime(start).TotalMilliseconds,
            Status = outcome.Status,
            StartedAt = startedAt,
        });

        return new AiToolResult(call.CallId, outcome.Result.Content, outcome.Result.IsError);
    }

    private async Task<Outcome> ExecuteAsync(string caller, AdjudicationContext run, AiToolCall call, CancellationToken ct)
    {
        var tool = registry.Find(call.ToolName);
        if (tool is null)
        {
            return Outcome.Denied("unknown_tool", NotAvailable(call.ToolName));
        }

        var descriptor = tool.Descriptor;
        var isAgent = caller != ToolDescriptor.ActionExecutorCaller;
        if (descriptor.SideEffect == ToolSideEffect.Consequential && isAgent)
        {
            await securityEvents.RecordAsync(
                SecurityEventKind.ToolScopeViolation,
                caller,
                $"tool:{descriptor.Name}",
                new { runId = run.RunId, claimId = run.ClaimId, principal = run.Tenant.PrincipalId },
                ct);
            return Outcome.Denied("consequential_tool", NotAvailable(call.ToolName));
        }

        if (!descriptor.AllowedCallers.Contains(caller))
        {
            return Outcome.Denied("caller_not_allowed", NotAvailable(call.ToolName));
        }

        var errors = SchemaErrors(descriptor, call.Arguments).Concat(ReferenceErrors(descriptor, call.Arguments, run.References)).ToList();
        if (errors.Count > 0)
        {
            return new Outcome("invalid_arguments", null, ToolResult.Error($"Invalid arguments: {string.Join("; ", errors)}"));
        }

        try
        {
            var result = await tool.InvokeAsync(
                call.Arguments, new ToolInvocationContext(run.Tenant, run.RunId, run.ClaimId, caller, run.References), ct);
            return new Outcome(result.IsError ? "error" : "ok", null, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Outcome("error", null, ToolResult.Error($"Tool '{descriptor.Name}' failed.") with { Summary = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    /// <summary>The same answer for unknown and disallowed tools, so the model learns nothing about other tools.</summary>
    private static string NotAvailable(string toolName) => $"Tool '{toolName}' is not available.";

    private static IEnumerable<string> SchemaErrors(ToolDescriptor descriptor, JsonElement arguments)
    {
        var schema = Schemas.GetOrAdd(
            $"{descriptor.Name}\n{descriptor.InputSchema.GetRawText()}",
            _ => JsonSchema.Build(descriptor.InputSchema, new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
        var evaluation = schema.Evaluate(arguments, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return evaluation.IsValid
            ? []
            : (evaluation.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .DefaultIfEmpty("the arguments do not match the input schema")
                .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> ReferenceErrors(ToolDescriptor descriptor, JsonElement arguments, ReferenceRegistry references)
    {
        foreach (var (argument, kind) in descriptor.ReferenceArguments ?? new Dictionary<string, ReferenceKind>())
        {
            if (arguments.TryGetProperty(argument, out var value) && value.ValueKind == JsonValueKind.String)
            {
                foreach (var error in references.Validate([value.GetString()!], kind))
                {
                    yield return $"{argument}: {error}";
                }
            }
        }
    }

    private string RedactedArguments(JsonElement arguments)
    {
        var redacted = redactor.Redact(arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText()).Text;
        return redacted.Length <= MaxRecordedArgumentsLength && IsJson(redacted)
            ? redacted
            : JsonSerializer.Serialize(new { truncated = redacted[..Math.Min(redacted.Length, MaxRecordedArgumentsLength)] });
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Outcome(string Status, string? DenialReason, ToolResult Result)
    {
        public static Outcome Denied(string reason, string message) => new("denied", reason, ToolResult.Error(message));
    }

    private sealed class ScopedInvoker(ToolInvoker invoker, string caller, AdjudicationContext run, IReadOnlyList<AiToolDefinition> definitions)
        : IToolInvoker
    {
        public IReadOnlyList<AiToolDefinition> Definitions => definitions;

        public Task<AiToolResult> InvokeAsync(AiToolCall call, CancellationToken ct) => invoker.InvokeAsync(caller, run, call, ct);
    }
}
