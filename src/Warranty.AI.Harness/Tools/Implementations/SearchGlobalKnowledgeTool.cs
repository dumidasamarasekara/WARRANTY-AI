using System.Globalization;
using System.Text.Json;
using Warranty.AI.Harness.Agents;
using Warranty.Application.Abstractions.Knowledge;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// <c>search_global_knowledge</c> (Policy, Decision): a free-text search of the platform-owned global
/// namespace only — terminology, generic fraud patterns and procedures, which hold no tenant or customer
/// data (contracts/rag.md). Snippets come back as <c>GLB-n</c> references, which are context only and
/// can never be cited as policy grounds (FR-006).
/// </summary>
public sealed class SearchGlobalKnowledgeTool(IKnowledgeRetriever retriever) : ITool
{
    public const int TopK = 5;

    public const string GlobalNamespace = "global";

    /// <summary>The document types of the global namespace.</summary>
    public static IReadOnlyList<DocumentType> GlobalDocumentTypes { get; } = [DocumentType.Terminology, DocumentType.FraudPattern, DocumentType.Procedure];

    private static readonly string Schema = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "query": { "type": "string", "description": "What to look for, e.g. 'definition of accidental damage'." },
            "documentType": {
              "type": "string",
              "enum": [{{string.Join(", ", GlobalDocumentTypes.Select(t => $"\"{t}\""))}}],
              "description": "Optional: search only this kind of document."
            }
          },
          "required": ["query"]
        }
        """;

    public ToolDescriptor Descriptor { get; } = new(
        ToolNames.SearchGlobalKnowledge,
        "Searches general warranty knowledge (terminology, generic fraud patterns, procedures). Returns snippets with GLB-n "
        + "references. Global knowledge is background only and must never be cited as policy grounds.",
        ToolSupport.Schema(Schema),
        ToolSideEffect.ReadOnly,
        ToolSupport.Callers(AgentNames.Policy, AgentNames.Decision));

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var query = arguments.GetProperty("query").GetString();
        if (KnowledgeToolSupport.QueryError(query) is { } error)
        {
            return ToolResult.Error(error);
        }

        IReadOnlyList<DocumentType> types = arguments.TryGetProperty("documentType", out var type)
            ? [Enum.Parse<DocumentType>(type.GetString()!, ignoreCase: false)]
            : GlobalDocumentTypes;
        var result = await retriever.SearchAsync(
            new KnowledgeSearchQuery(query!.Trim(), KnowledgeScope.Global, types, null, TopK, new RetrievalAttribution(ctx.Caller, ctx.RunId, ctx.ClaimId)),
            ct);
        if (result.Outcome == RetrievalOutcome.ScopeViolation)
        {
            return ToolResult.Error(KnowledgeToolSupport.Unavailable);
        }

        // Global chunks only, so a tenant chunk can never be handed out as GLB-n or as POL-n through this tool.
        var snippets = result.Chunks
            .Where(c => string.Equals(c.Namespace, GlobalNamespace, StringComparison.Ordinal))
            .Select(c => new GlobalKnowledgeSnippet(ctx.References.IssueChunk(c), c.DocumentTitle, c.SectionTitle, c.Text))
            .ToList();
        return ToolResult.Ok(
            new GlobalSearchResult(snippets, "Context only: GLB-n snippets are not policy and must not be cited as policy grounds."),
            string.Create(CultureInfo.InvariantCulture, $"{snippets.Count} snippets: {string.Join(", ", snippets.Select(s => s.Ref))}"));
    }
}

/// <summary>A global knowledge snippet; <paramref name="Ref"/> is its <c>GLB-n</c> reference.</summary>
public sealed record GlobalKnowledgeSnippet(string Ref, string DocumentTitle, string? SectionTitle, string Text);

public sealed record GlobalSearchResult(IReadOnlyList<GlobalKnowledgeSnippet> Snippets, string Note);
