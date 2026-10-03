using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Tools;

/// <summary>
/// All registered tools. Rejects duplicate names and descriptors that break the strict-schema or
/// no-tenant-input rules. <see cref="For"/> is what an agent may be offered: only ReadOnly tools whose
/// allowed callers include the agent — Consequential tools are never offered to any model.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var problems = tool.Descriptor.Problems();
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"Tool '{tool.Descriptor.Name}' is invalid: {string.Join("; ", problems)}.");
            }

            if (!_tools.TryAdd(tool.Descriptor.Name, tool))
            {
                throw new InvalidOperationException($"Tool '{tool.Descriptor.Name}' is registered twice.");
            }
        }
    }

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public ITool? Find(string name) => _tools.GetValueOrDefault(name ?? string.Empty);

    /// <summary>The ReadOnly tools <paramref name="agentName"/> may call, in name order.</summary>
    public IReadOnlyList<ITool> For(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        return _tools.Values
            .Where(t => t.Descriptor.SideEffect == ToolSideEffect.ReadOnly && t.Descriptor.AllowedCallers.Contains(agentName))
            .OrderBy(t => t.Descriptor.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The model-facing definitions of <see cref="For"/>.</summary>
    public IReadOnlyList<AiToolDefinition> DefinitionsFor(string agentName)
        => For(agentName).Select(t => new AiToolDefinition(t.Descriptor.Name, t.Descriptor.Description, t.Descriptor.InputSchema)).ToList();
}
