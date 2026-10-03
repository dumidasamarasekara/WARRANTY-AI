namespace Warranty.Infrastructure.Persistence.Sql;

/// <summary>
/// The idempotent owner-level SQL scripts embedded from <c>Persistence/Sql/*.sql</c> (roles, row-level
/// security, security-definer functions). The migration service runs them in file-name order after
/// the EF Core migrations.
/// </summary>
public static class SqlScripts
{
    private const string Prefix = "Warranty.Infrastructure.Persistence.Sql.";

    /// <summary>Scripts as (file name, SQL), ordered by file name.</summary>
    public static IReadOnlyList<(string Name, string Sql)> Load()
    {
        var assembly = typeof(SqlScripts).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n[Prefix.Length..], reader.ReadToEnd());
            })
            .ToList();
    }
}
