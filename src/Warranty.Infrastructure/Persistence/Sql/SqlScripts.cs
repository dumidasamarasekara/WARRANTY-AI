namespace Warranty.Infrastructure.Persistence.Sql;

/// <summary>The two PostgreSQL databases: transactional data and the knowledge store.</summary>
public enum SqlDatabase
{
    Warranty,
    Knowledge,
}

/// <summary>
/// The idempotent owner-level SQL scripts embedded from <c>Persistence/Sql/*.sql</c> (roles, row-level
/// security, the knowledge schema, security-definer functions). Each script names its database on its
/// first line (<c>-- database: warranty</c> or <c>-- database: knowledge</c>). The migration service
/// runs them in file-name order after the EF Core migrations.
/// </summary>
public static class SqlScripts
{
    private const string Prefix = "Warranty.Infrastructure.Persistence.Sql.";
    private const string DatabaseDirective = "-- database:";

    /// <summary>Scripts for <paramref name="database"/> as (file name, SQL), ordered by file name.</summary>
    public static IReadOnlyList<(string Name, string Sql)> Load(SqlDatabase database)
        => LoadAll().Where(s => s.Database == database).Select(s => (s.Name, s.Sql)).ToList();

    private static IEnumerable<(string Name, SqlDatabase Database, string Sql)> LoadAll()
    {
        var assembly = typeof(SqlScripts).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            var name = resource[Prefix.Length..];
            yield return (name, DatabaseOf(name, sql), sql);
        }
    }

    private static SqlDatabase DatabaseOf(string name, string sql)
    {
        var firstLine = sql.AsSpan().TrimStart('﻿');
        var end = firstLine.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            firstLine = firstLine[..end];
        }

        return firstLine.StartsWith(DatabaseDirective, StringComparison.Ordinal)
            && Enum.TryParse<SqlDatabase>(firstLine[DatabaseDirective.Length..].Trim(), ignoreCase: true, out var database)
                ? database
                : throw new InvalidOperationException($"SQL script {name} must start with '{DatabaseDirective} warranty|knowledge'.");
    }
}
