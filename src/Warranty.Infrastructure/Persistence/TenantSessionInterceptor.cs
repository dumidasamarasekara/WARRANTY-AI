using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Warranty.Application.Abstractions;

namespace Warranty.Infrastructure.Persistence;

/// <summary>
/// Binds every database connection to the current tenant: on open it sets <c>app.tenant_id</c> and
/// <c>app.kb_namespace</c> from <see cref="ITenantContext"/>, which the row-level security policies of
/// the transactional and knowledge databases read; on close it clears both, so a pooled connection
/// never carries a tenant into its next use (research R8). Opening a connection without a resolved
/// tenant throws, unless the flow is inside a <see cref="NoTenantScope"/>.
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    internal const string ApplySql =
        "select set_config('app.tenant_id', @tenant, false), set_config('app.kb_namespace', @ns, false)";

    internal const string ResetSql =
        "select set_config('app.tenant_id', '', false), set_config('app.kb_namespace', '', false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateOpenCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateOpenCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override InterceptionResult ConnectionClosing(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (connection.State == ConnectionState.Open)
        {
            using var command = CreateCommand(connection, ResetSql);
            try
            {
                command.ExecuteNonQuery();
            }
            catch (DbException)
            {
                // A connection that cannot run the reset is broken or inside an aborted transaction;
                // Npgsql discards it or resets its session state when it returns to the pool, and the
                // next open sets the tenant again before any query runs.
            }
        }

        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (connection.State == ConnectionState.Open)
        {
            await using var command = CreateCommand(connection, ResetSql);
            try
            {
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (DbException)
            {
                // As in ConnectionClosing.
            }
        }

        return result;
    }

    private DbCommand CreateOpenCommand(DbConnection connection)
    {
        if (NoTenantScope.IsActive)
        {
            return CreateCommand(connection, ResetSql);
        }

        if (!tenantContext.IsResolved)
        {
            throw new InvalidOperationException(
                "A database connection was opened without a tenant context. Tenant-scoped data access " +
                "needs a resolved ITenantContext (request or TenantContextScope.Begin).");
        }

        var command = CreateCommand(connection, ApplySql);
        AddParameter(command, "tenant", tenantContext.TenantId.ToString());
        AddParameter(command, "ns", tenantContext.KnowledgeNamespace);
        return command;
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
