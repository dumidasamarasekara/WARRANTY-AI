namespace Warranty.Application.Abstractions;

/// <summary>
/// An ambient tenant context for code that runs outside an HTTP request (the claim job worker and
/// the migration service). <see cref="Begin"/> flows through async calls via <see cref="AsyncLocal{T}"/>
/// and restores the previous scope on dispose, so concurrent jobs never see each other's tenant.
/// </summary>
public sealed class TenantContextScope : ITenantContext, IDisposable
{
    private static readonly AsyncLocal<TenantContextScope?> CurrentScope = new();

    private readonly TenantContextScope? _previous;
    private bool _disposed;

    private TenantContextScope(
        Guid tenantId, string tenantSlug, string principalId, string principalName, IEnumerable<string> roles,
        string correlationId, bool isSystem)
    {
        TenantId = tenantId;
        TenantSlug = tenantSlug;
        PrincipalId = principalId;
        PrincipalName = principalName;
        Roles = roles.ToHashSet(StringComparer.Ordinal);
        CorrelationId = correlationId;
        IsSystem = isSystem;
        _previous = CurrentScope.Value;
    }

    /// <summary>The innermost active scope on this async flow, if any.</summary>
    public static TenantContextScope? Current => CurrentScope.Value;

    public bool IsResolved => true;

    public Guid TenantId { get; }

    public string TenantSlug { get; }

    public string KnowledgeNamespace => $"tenant-{TenantSlug}";

    public string PrincipalId { get; }

    public string PrincipalName { get; }

    public IReadOnlySet<string> Roles { get; }

    public string CorrelationId { get; }

    public bool IsSystem { get; }

    /// <summary>Opens a scope for one job or seeding step; dispose it when the work ends.</summary>
    public static TenantContextScope Begin(
        Guid tenantId,
        string tenantSlug,
        string principal = Principals.AdjudicationService,
        string? correlationId = null,
        IEnumerable<string>? roles = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant scope needs a tenant.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);

        var isSystem = principal == Principals.AdjudicationService;
        var scope = new TenantContextScope(
            tenantId,
            tenantSlug,
            principal,
            principal,
            roles ?? (isSystem ? [Principals.AdjudicationService] : []),
            correlationId ?? Guid.CreateVersion7().ToString("N"),
            isSystem);
        CurrentScope.Value = scope;
        return scope;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(CurrentScope.Value, this))
        {
            CurrentScope.Value = _previous;
        }
    }
}
