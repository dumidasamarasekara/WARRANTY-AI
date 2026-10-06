using Npgsql;

namespace Warranty.Evaluation.Metrics;

/// <summary>Review decisions of one tenant: all, those on a valid APPROVE/REJECT recommendation, and overrides.</summary>
public sealed record OverrideRateRow(string Tenant, int Decisions, int OnAiDecisions, int Overrides)
{
    /// <summary>Overrides / decisions on a valid AI approve or reject (the only ones that can override, FR-035).</summary>
    public double? Rate => OnAiDecisions == 0 ? null : (double)Overrides / OnAiDecisions;
}

public sealed record HumanOverrideRateResult(string Source, IReadOnlyList<OverrideRateRow> Tenants)
{
    public OverrideRateRow Total => new(
        "all",
        Tenants.Sum(t => t.Decisions),
        Tenants.Sum(t => t.OnAiDecisions),
        Tenants.Sum(t => t.Overrides));
}

/// <summary>
/// Human override rate (research R19): reported from review decisions (<c>review.review_decisions.overrides_ai</c>),
/// not from the golden cases — the evaluation never records reviewer decisions. Run against a production
/// (or staging) database with a login that reads every tenant's rows; against the evaluation database it
/// reports whatever decisions it holds (normally none).
/// </summary>
public static class HumanOverrideRate
{
    public const string Sql = """
        select t.slug,
               count(*)::int as decisions,
               count(*) filter (where r.is_valid and r.decision in ('APPROVE', 'REJECT'))::int as on_ai_decisions,
               count(*) filter (where d.overrides_ai)::int as overrides
        from review.review_decisions d
        join tenancy.tenants t on t.id = d.tenant_id
        left join adjudication.recommendations r on r.tenant_id = d.tenant_id and r.run_id = d.run_id
        group by t.slug
        order by t.slug
        """;

    public static async Task<HumanOverrideRateResult> QueryAsync(string connectionString, string source, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<OverrideRateRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OverrideRateRow(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return new HumanOverrideRateResult(source, rows);
    }
}
