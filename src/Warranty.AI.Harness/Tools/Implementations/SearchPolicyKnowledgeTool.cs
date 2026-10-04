using System.Globalization;
using System.Text.Json;
using Warranty.AI.Harness.Agents;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// <c>search_policy_knowledge</c> (Policy): a free-text search for further clauses of the tenant's
/// warranty policies, with the same hard filters as the run's policy retrieval (product category and
/// model, region, purchase date — contracts/rag.md). Only the tenant namespace is searched; the namespace
/// and roles come from the tenant context, never from the arguments. Each clause is returned under a
/// <c>POL-n</c> reference issued by the run's <c>ReferenceRegistry</c>, so it can be cited.
/// </summary>
public sealed class SearchPolicyKnowledgeTool(IKnowledgeRetriever retriever, IClaimRepository claims, ICatalogRepository catalog) : ITool
{
    public const int TopK = 5;

    private const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "query": { "type": "string", "description": "What to look for in the warranty policy, e.g. 'battery coverage period'." }
          },
          "required": ["query"]
        }
        """;

    public ToolDescriptor Descriptor { get; } = new(
        ToolNames.SearchPolicyKnowledge,
        "Searches the manufacturer's warranty policy that applies to this claim for additional clauses. Returns clauses with "
        + "POL-n references that may be cited as policy grounds.",
        ToolSupport.Schema(Schema),
        ToolSideEffect.ReadOnly,
        ToolSupport.Callers(AgentNames.Policy));

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (KnowledgeToolSupport.QueryError(arguments.GetProperty("query").GetString()) is { } error)
        {
            return ToolResult.Error(error);
        }

        var claim = await ToolSupport.RequireClaimAsync(claims, ctx.ClaimId, ct);
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var result = await retriever.SearchAsync(
            new KnowledgeSearchQuery(
                arguments.GetProperty("query").GetString()!.Trim(),
                KnowledgeScope.Tenant,
                [DocumentType.WarrantyPolicy],
                ApplicabilityFor(claim, product),
                TopK,
                new RetrievalAttribution(ctx.Caller, ctx.RunId, ctx.ClaimId)),
            ct);
        if (result.Outcome == RetrievalOutcome.ScopeViolation)
        {
            return ToolResult.Error(KnowledgeToolSupport.Unavailable);
        }

        // The tenant namespace only; the retriever's assertion already enforces it, this keeps GLB-n out of POL-n results.
        var clauses = result.Chunks
            .Where(c => string.Equals(c.Namespace, ctx.Tenant.KnowledgeNamespace, StringComparison.Ordinal))
            .Select(c => new PolicyClauseSnippet(
                ctx.References.IssueChunk(c),
                c.ClauseKey,
                c.SectionTitle,
                c.DocumentTitle,
                c.Version,
                c.EffectiveFrom,
                c.EffectiveTo,
                c.ClauseType?.ToString(),
                c.ExclusionCode is { } code ? WireName.Of(code) : null,
                c.Text))
            .ToList();
        return ToolResult.Ok(
            new PolicySearchResult(clauses),
            string.Create(CultureInfo.InvariantCulture, $"{clauses.Count} clauses: {string.Join(", ", clauses.Select(c => c.Ref))}"));
    }

    /// <summary>The policy filters of a claim, as used by the run's policy retrieval.</summary>
    public static PolicyApplicability ApplicabilityFor(Claim claim, Product? product)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new PolicyApplicability(product?.Category, product?.ModelCode ?? claim.ProductModelCode, claim.Region, claim.PurchaseDate);
    }
}

/// <summary>A tenant policy clause returned by <c>search_policy_knowledge</c>.</summary>
/// <param name="Ref">The <c>POL-n</c> reference to cite.</param>
/// <param name="ClauseKey">E.g. <c>AUR-WP-2.1</c>.</param>
/// <param name="SectionTitle">Clause heading.</param>
/// <param name="DocumentTitle">Policy title.</param>
/// <param name="Version">Policy version.</param>
/// <param name="EffectiveFrom">Version start.</param>
/// <param name="EffectiveTo">Version end; null when open-ended.</param>
/// <param name="ClauseType"><c>Coverage</c>, <c>Period</c>, <c>Exclusion</c>, <c>ServiceRule</c> or <c>Definition</c>.</param>
/// <param name="ExclusionCode">For exclusion clauses, e.g. <c>ACCIDENTAL_DAMAGE</c>.</param>
/// <param name="Text">Clause text.</param>
public sealed record PolicyClauseSnippet(
    string Ref,
    string? ClauseKey,
    string? SectionTitle,
    string DocumentTitle,
    int Version,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    string? ClauseType,
    string? ExclusionCode,
    string Text);

public sealed record PolicySearchResult(IReadOnlyList<PolicyClauseSnippet> Clauses);

/// <summary>Argument checks shared by the knowledge search tools.</summary>
internal static class KnowledgeToolSupport
{
    public const int MaxQueryLength = 500;

    public const string Unavailable = "Knowledge search is not available for this run.";

    public static string? QueryError(string? query)
        => string.IsNullOrWhiteSpace(query) || query.Length > MaxQueryLength
            ? $"query must be non-blank and at most {MaxQueryLength} characters."
            : null;
}
