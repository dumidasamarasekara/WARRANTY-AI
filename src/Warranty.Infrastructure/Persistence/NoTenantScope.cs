namespace Warranty.Infrastructure.Persistence;

/// <summary>
/// Tags the database connections opened on the current async flow as exempt from the tenant session:
/// <see cref="TenantSessionInterceptor"/> then clears <c>app.tenant_id</c> and <c>app.kb_namespace</c>
/// instead of requiring a tenant context. Internal on purpose: only the migration service (seeding
/// platform tables, via <c>InternalsVisibleTo</c>) and <c>PostgresJobQueue.TryDequeueAsync</c> (which
/// reads only job headers through a <c>SECURITY DEFINER</c> function) may open such connections
/// (research R8, R12). With no tenant set, row-level security still returns no tenant rows.
/// </summary>
internal static class NoTenantScope
{
    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>True while a <see cref="Begin"/> scope is open on this async flow.</summary>
    public static bool IsActive => Active.Value;

    /// <summary>Opens the exemption; dispose it as soon as the connection work ends.</summary>
    public static IDisposable Begin()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Active.Value = previous;
        }
    }
}
