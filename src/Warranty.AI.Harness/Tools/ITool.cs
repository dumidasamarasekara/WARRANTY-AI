using System.Text.Json;
using Warranty.AI.Harness.Context;
using Warranty.Application.Abstractions;

namespace Warranty.AI.Harness.Tools;

/// <summary>A tool that agents (ReadOnly) or the action executor (Consequential) can call.</summary>
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    /// <summary>Runs with arguments already validated against <see cref="ToolDescriptor.InputSchema"/> and references already checked.</summary>
    Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct);
}

/// <summary>
/// Injected by the harness for every invocation. The tenant comes from the run's job context only;
/// tools never accept tenant input.
/// </summary>
public sealed record ToolInvocationContext(ITenantContext Tenant, Guid RunId, Guid ClaimId, string Caller, ReferenceRegistry References);

/// <summary>A tool's answer to the model; <see cref="Summary"/> is the short, redacted text stored in <c>aiops.tool_calls</c>.</summary>
public sealed record ToolResult(bool IsError, JsonElement Content, string Summary)
{
    public static ToolResult Ok(object content, string summary) => new(false, JsonSerializer.SerializeToElement(content, ToolJson.Options), summary);

    public static ToolResult Error(string message) => new(true, JsonSerializer.SerializeToElement(new { error = message }, ToolJson.Options), message);
}

/// <summary>JSON settings shared by tool results.</summary>
public static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
