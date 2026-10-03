namespace Warranty.Domain.Policies;

/// <summary>
/// One clause of a policy version. Exclusion clauses carry a structured <see cref="ExclusionCode"/>
/// that must be listed in the version's terms, so exclusion grounding never depends on wording
/// (research R26).
/// </summary>
public sealed class PolicyClause
{
    private PolicyClause()
    {
        ClauseKey = Title = Text = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PolicyVersionId { get; private set; }

    /// <summary>Stable key, unique per version, e.g. <c>AUR-WP-3.2</c>.</summary>
    public string ClauseKey { get; private set; }

    public ClauseType ClauseType { get; private set; }

    /// <summary>Present exactly when <see cref="ClauseType"/> is <see cref="ClauseType.Exclusion"/>.</summary>
    public ExclusionCode? ExclusionCode { get; private set; }

    public string Title { get; private set; }

    /// <summary>Clause wording; the source of truth also indexed in the knowledge database.</summary>
    public string Text { get; private set; }

    public static PolicyClause Create(
        Guid id,
        PolicyVersion version,
        string clauseKey,
        ClauseType clauseType,
        ExclusionCode? exclusionCode,
        string title,
        string text)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Clause ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(clauseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (clauseType == ClauseType.Exclusion)
        {
            if (exclusionCode is null)
            {
                throw new ArgumentException($"Exclusion clause {clauseKey} must declare an exclusion code.", nameof(exclusionCode));
            }

            if (!version.Terms.Excludes(exclusionCode.Value))
            {
                throw new ArgumentException(
                    $"Exclusion clause {clauseKey} uses {exclusionCode}, which version {version.Version} does not list in terms.exclusions.",
                    nameof(exclusionCode));
            }
        }
        else if (exclusionCode is not null)
        {
            throw new ArgumentException($"Only exclusion clauses may declare an exclusion code ({clauseKey} is {clauseType}).", nameof(exclusionCode));
        }

        return new PolicyClause
        {
            Id = id,
            TenantId = version.TenantId,
            PolicyVersionId = version.Id,
            ClauseKey = clauseKey.Trim(),
            ClauseType = clauseType,
            ExclusionCode = exclusionCode,
            Title = title.Trim(),
            Text = text.Trim(),
        };
    }
}
