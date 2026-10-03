using System.Text.Json;
using Warranty.AI.Harness.Context;

namespace Warranty.AI.Harness.Tools;

public enum ToolSideEffect
{
    /// <summary>Reads data of the current tenant; may be offered to models.</summary>
    ReadOnly,

    /// <summary>Changes state outside the run; never offered to any model, only to the action executor.</summary>
    Consequential,
}

/// <summary>
/// A tool's contract (contracts/agents-and-tools.md). The input schema is strict — an object with
/// <c>additionalProperties: false</c> — and has no tenant fields: tools receive the tenant only through
/// <see cref="ToolInvocationContext"/>. <paramref name="ReferenceArguments"/> names top-level string
/// arguments that carry harness references (e.g. <c>invoiceRef</c> → <see cref="ReferenceKind.Evidence"/>);
/// the invoker rejects any value that was not issued for the run.
/// </summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolSideEffect SideEffect,
    IReadOnlySet<string> AllowedCallers,
    IReadOnlyDictionary<string, ReferenceKind>? ReferenceArguments = null)
{
    /// <summary>The caller name of the action executor, the only caller of Consequential tools.</summary>
    public const string ActionExecutorCaller = "action-executor";

    /// <summary>
    /// Checks the strict-schema and no-tenant-input rules; the registry calls it for every tool. Returns
    /// the problems found, or none.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || !Name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            problems.Add($"tool name '{Name}' must be snake_case");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            problems.Add("a description is required");
        }

        if (AllowedCallers.Count == 0)
        {
            problems.Add("at least one allowed caller is required");
        }

        if (InputSchema.ValueKind != JsonValueKind.Object
            || !InputSchema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object")
        {
            problems.Add("the input schema must be of type object");
            return problems;
        }

        if (!InputSchema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
        {
            problems.Add("the input schema must set additionalProperties to false");
        }

        var properties = InputSchema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared.EnumerateObject().Select(p => p.Name).ToList()
            : [];
        problems.AddRange(properties
            .Where(p => p.Contains("tenant", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"input property '{p}' looks like a tenant field; tools never accept tenant input"));
        problems.AddRange((ReferenceArguments?.Keys ?? [])
            .Where(argument => !properties.Contains(argument, StringComparer.Ordinal))
            .Select(argument => $"reference argument '{argument}' is not an input property"));
        return problems;
    }
}
