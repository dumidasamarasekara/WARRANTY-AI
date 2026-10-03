using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Warranty.Infrastructure;
using Warranty.Infrastructure.Persistence;
using Warranty.Infrastructure.Persistence.Knowledge;
using Warranty.Infrastructure.Persistence.Sql;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// Brings both databases to the current schema as their owner: the EF Core migrations of
/// <c>warranty</c>, then the owner-level SQL scripts of each database in file-name order (roles,
/// row-level security, the knowledge schema and functions), the <c>warranty_app</c> password, and
/// one knowledge partition per namespace. Every step is idempotent.
/// </summary>
internal sealed class DatabaseMigrator(
    WarrantyDbContext warranty,
    KnowledgeDbContext knowledge,
    IConfiguration configuration,
    ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync(IReadOnlyList<string> knowledgeNamespaces, CancellationToken ct)
    {
        using var platform = NoTenantScope.Begin();

        await warranty.Database.MigrateAsync(ct);
        logger.LogInformation("Applied the EF Core migrations of database 'warranty'");
        await RunScriptsAsync(warranty, SqlDatabase.Warranty, ct);

        // The knowledge schema is owned by its SQL script, not by migrations; only the database is created here.
        var creator = knowledge.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct))
        {
            await creator.CreateAsync(ct);
        }

        await RunScriptsAsync(knowledge, SqlDatabase.Knowledge, ct);
        await SetAppRolePasswordAsync(ct);

        foreach (var ns in knowledgeNamespaces)
        {
            await knowledge.Database.ExecuteSqlAsync($"SELECT knowledge.ensure_namespace({ns})", ct);
        }

        logger.LogInformation("Knowledge partitions ready: {Namespaces}", string.Join(", ", knowledgeNamespaces));
    }

    private async Task RunScriptsAsync(DbContext db, SqlDatabase database, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            foreach (var (name, sql) in SqlScripts.Load(database))
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(ct);
                logger.LogInformation("Applied SQL script {Script}", name);
            }

            // Types created by the scripts (e.g. vector) must be visible to this connection's later commands.
            await connection.ReloadTypesAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Sets the app role's password from configuration; the server quotes it, so it is never spliced into SQL.</summary>
    private async Task SetAppRolePasswordAsync(CancellationToken ct)
    {
        var password = configuration[DependencyInjection.AppRolePasswordKey];
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"'{DependencyInjection.AppRolePasswordKey}' is not configured; the API could not connect as {DependencyInjection.AppRole}.");
        }

        await warranty.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)warranty.Database.GetDbConnection();
            await using var format = new NpgsqlCommand("SELECT format('ALTER ROLE %I PASSWORD %L', @role, @password)", connection);
            format.Parameters.AddWithValue("role", DependencyInjection.AppRole);
            format.Parameters.AddWithValue("password", password);
            var statement = (string)(await format.ExecuteScalarAsync(ct))!;

            await using var alter = new NpgsqlCommand(statement, connection);
            await alter.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await warranty.Database.CloseConnectionAsync();
        }

        logger.LogInformation("Set the password of role {Role}", DependencyInjection.AppRole);
    }
}
