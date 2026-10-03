using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Context;

/// <summary>Assembly priority of a context item; lower values are kept first (contracts/agents-and-tools.md).</summary>
public enum ContextPriority
{
    Instructions = 0,
    CaseFacts = 1,

    /// <summary><c>Period</c> and <c>Exclusion</c> clauses of the selected version.</summary>
    DecisiveClauses = 2,

    /// <summary>Other clauses, kept in descending score order.</summary>
    OtherClauses = 3,

    /// <summary><c>GLB-n</c> snippets; dropped first.</summary>
    GlobalSnippets = 4,
}

/// <summary>
/// One block of an agent's user turn. <see cref="IsEvidence"/> marks evidence documents and their
/// text, which are required: they are never dropped or truncated.
/// </summary>
public sealed record ContextItem(string Key, ContextPriority Priority, IReadOnlyList<AiContentPart> Parts, double Score = 0, bool IsEvidence = false)
{
    public static ContextItem Text(string key, ContextPriority priority, string text, double score = 0)
        => new(key, priority, [new TextPart(text)], score);

    /// <summary>Instructions and case facts are required, like evidence; clauses and snippets may be dropped.</summary>
    public bool IsRequired => IsEvidence || Priority is ContextPriority.Instructions or ContextPriority.CaseFacts;
}

public enum ContextBuildStatus
{
    Ok,

    /// <summary>The required items alone exceed the budget; the step fails to human review instead of truncating evidence.</summary>
    ContextOverflow,
}

/// <summary>The assembled user turn, what was dropped to fit, and the estimate it was measured with.</summary>
public sealed record ContextBuildResult(
    ContextBuildStatus Status,
    IReadOnlyList<AiContentPart> Parts,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Dropped,
    int EstimatedTokens,
    int Budget)
{
    public bool IsOverflow => Status == ContextBuildStatus.ContextOverflow;
}

/// <summary>
/// Token-aware assembly of an agent's user turn. Items are ordered instructions → case facts →
/// Period/Exclusion clauses → other clauses by score → global snippets. When the estimate exceeds the
/// agent's budget, optional items are dropped from the lowest priority (and, within a priority, the
/// lowest score) upwards. Required items — instructions, case facts and evidence — are never dropped or
/// truncated; if they alone do not fit, the result is <see cref="ContextBuildStatus.ContextOverflow"/>.
/// Images and PDFs count as zero here: attachments sit outside the text budget (budget table: "4k + attachment").
/// </summary>
public sealed class ContextBuilder
{
    /// <summary>Rough characters per token for budget estimates; deliberately conservative.</summary>
    public const int CharsPerToken = 4;

    /// <summary>Per-part overhead for separators and wrapping tags.</summary>
    public const int PartOverheadTokens = 8;

    public ContextBuildResult Build(IEnumerable<ContextItem> items, int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenBudget);

        var ordered = items
            .Select((item, index) => (Item: item, Index: index))
            .OrderBy(x => x.Item.Priority)
            .ThenByDescending(x => x.Item.Priority == ContextPriority.OtherClauses ? x.Item.Score : 0)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .ToList();

        var duplicate = ordered.GroupBy(i => i.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Context item '{duplicate.Key}' was added twice.", nameof(items));
        }

        var kept = ordered.ToList();
        var total = kept.Sum(Estimate);
        var required = kept.Where(i => i.IsRequired).Sum(Estimate);
        if (required > tokenBudget)
        {
            return new ContextBuildResult(
                ContextBuildStatus.ContextOverflow, [], [], ordered.Select(i => i.Key).ToList(), required, tokenBudget);
        }

        // Lowest priority first; within a priority, the lowest-ranked (last in order) first.
        var dropOrder = ordered.Where(i => !i.IsRequired)
            .Select((item, index) => (Item: item, Index: index))
            .OrderByDescending(x => x.Item.Priority)
            .ThenByDescending(x => x.Index)
            .Select(x => x.Item);
        var dropped = new List<string>();
        foreach (var item in dropOrder)
        {
            if (total <= tokenBudget)
            {
                break;
            }

            kept.Remove(item);
            dropped.Add(item.Key);
            total -= Estimate(item);
        }

        return new ContextBuildResult(
            ContextBuildStatus.Ok, kept.SelectMany(i => i.Parts).ToList(), kept.Select(i => i.Key).ToList(), dropped, total, tokenBudget);
    }

    /// <summary>The token estimate of one item.</summary>
    public static int Estimate(ContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Parts.Sum(Estimate);
    }

    public static int Estimate(AiContentPart part) => part switch
    {
        TextPart text => Tokens(text.Text.Length),
        UntrustedTextPart untrusted => Tokens(untrusted.Label.Length + untrusted.Text.Length),
        ToolCallPart call => Tokens(call.ToolName.Length + call.Arguments.GetRawText().Length),
        ToolResultPart result => Tokens(result.Result.GetRawText().Length),
        ImagePart or DocumentPart or ProviderOpaquePart => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(part), part.GetType().Name, "Unknown content part."),
    };

    private static int Tokens(int chars) => PartOverheadTokens + ((chars + CharsPerToken - 1) / CharsPerToken);
}
