using Npgsql;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.AiOps;
using Warranty.Domain.Review;

namespace Warranty.Infrastructure.Persistence.Repositories;

internal sealed class AdjudicationRepository(WarrantyDbContext db) : IAdjudicationRepository
{
    public void AddRun(AdjudicationRun run) => db.AdjudicationRuns.Add(run);

    public Task<AdjudicationRun?> GetRunAsync(Guid claimId, int round, CancellationToken ct)
        => db.AdjudicationRuns.SingleOrDefaultAsync(r => r.ClaimId == claimId && r.Round == round, ct);

    public Task<AdjudicationRun?> GetLatestRunAsync(Guid claimId, CancellationToken ct)
        => db.AdjudicationRuns.Where(r => r.ClaimId == claimId).OrderByDescending(r => r.Round).FirstOrDefaultAsync(ct);

    public void AddIntakeResult(IntakeResult result) => db.IntakeResults.Add(result);

    public void AddEvidenceFinding(EvidenceFinding finding) => db.EvidenceFindings.Add(finding);

    public void AddRetrievedPolicyRef(RetrievedPolicyRef reference) => db.RetrievedPolicyRefs.Add(reference);

    public void AddPolicyAssessment(PolicyAssessment assessment) => db.PolicyAssessments.Add(assessment);

    public void AddRiskAssessment(RiskAssessment assessment) => db.RiskAssessments.Add(assessment);

    public void AddRecommendation(Recommendation recommendation) => db.Recommendations.Add(recommendation);

    public void AddGuardrailEvaluation(GuardrailEvaluation evaluation) => db.GuardrailEvaluations.Add(evaluation);

    public async Task<RunRecord?> GetRunRecordAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.AdjudicationRuns.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return null;
        }

        return new RunRecord(
            run,
            await db.IntakeResults.SingleOrDefaultAsync(i => i.RunId == runId, ct),
            await db.EvidenceFindings.Where(f => f.RunId == runId).OrderBy(f => f.Kind).ThenBy(f => f.EvidenceId).ToListAsync(ct),
            await db.RetrievedPolicyRefs.Where(r => r.RunId == runId).OrderBy(r => r.RefId.Length).ThenBy(r => r.RefId).ToListAsync(ct),
            await db.PolicyAssessments.SingleOrDefaultAsync(a => a.RunId == runId, ct),
            await db.RiskAssessments.SingleOrDefaultAsync(r => r.RunId == runId, ct),
            await db.Recommendations.SingleOrDefaultAsync(r => r.RunId == runId, ct),
            await db.GuardrailEvaluations.SingleOrDefaultAsync(g => g.RunId == runId, ct));
    }

    public async Task<IReadOnlyDictionary<Guid, LatestRunOutcome>> GetLatestOutcomesAsync(IReadOnlyCollection<Guid> claimIds, CancellationToken ct)
    {
        if (claimIds.Count == 0)
        {
            return new Dictionary<Guid, LatestRunOutcome>();
        }

        var ids = claimIds.Distinct().ToArray();
        var latest = (await db.AdjudicationRuns.AsNoTracking()
                .Where(r => ids.Contains(r.ClaimId))
                .Select(r => new { r.Id, r.ClaimId, r.Round, r.Disposition })
                .ToListAsync(ct))
            .GroupBy(r => r.ClaimId)
            .Select(g => g.MaxBy(r => r.Round)!)
            .ToList();

        var runIds = latest.Select(r => r.Id).ToArray();
        var decisions = await db.Recommendations.AsNoTracking()
            .Where(r => runIds.Contains(r.RunId))
            .Select(r => new { r.RunId, r.Decision })
            .ToDictionaryAsync(r => r.RunId, r => r.Decision, ct);

        return latest.ToDictionary(
            r => r.ClaimId,
            r => new LatestRunOutcome(r.ClaimId, r.Id, decisions.GetValueOrDefault(r.Id), r.Disposition));
    }
}

internal sealed class ReviewRepository(WarrantyDbContext db) : IReviewRepository
{
    public void Add(ReviewDecision decision) => db.ReviewDecisions.Add(decision);

    public async Task<IReadOnlyList<ReviewDecision>> GetForClaimAsync(Guid claimId, CancellationToken ct)
        => await db.ReviewDecisions.Where(d => d.ClaimId == claimId).OrderBy(d => d.DecidedAt).ToListAsync(ct);
}

/// <summary>
/// AI operations records join the unit of work like any other row, but they are recorded from parallel
/// model calls (one per photo in the Evidence step), and a <c>DbContext</c> is not thread-safe: concurrent
/// <c>Add</c> calls corrupt its change tracking (a lost update of the changed-entity count makes a later
/// <c>SaveChanges</c> skip pending rows). The records are therefore queued thread-safely and handed to
/// the context on the saving thread, when the next <c>SaveChanges</c> starts.
/// </summary>
internal sealed class AiOpsRepository : IAiOpsRepository
{
    private readonly WarrantyDbContext _db;
    private readonly ConcurrentQueue<object> _pending = new();

    public AiOpsRepository(WarrantyDbContext db)
    {
        _db = db;
        _db.SavingChanges += (_, _) => Flush();
    }

    public void AddModelCall(ModelCall call) => _pending.Enqueue(call ?? throw new ArgumentNullException(nameof(call)));

    public void AddToolCall(ToolCall call) => _pending.Enqueue(call ?? throw new ArgumentNullException(nameof(call)));

    public void AddRagQuery(RagQuery query) => _pending.Enqueue(query ?? throw new ArgumentNullException(nameof(query)));

    public async Task<AiOpsRecords> GetForRunAsync(Guid runId, CancellationToken ct)
        => new(
            await _db.ModelCalls.AsNoTracking().Where(c => c.RunId == runId).OrderBy(c => c.StartedAt).ToListAsync(ct),
            await _db.ToolCalls.AsNoTracking().Where(c => c.RunId == runId).OrderBy(c => c.StartedAt).ToListAsync(ct),
            await _db.RagQueries.AsNoTracking().Where(q => q.RunId == runId).OrderBy(q => q.StartedAt).ToListAsync(ct));

    private void Flush()
    {
        while (_pending.TryDequeue(out var record))
        {
            _db.Add(record);
        }
    }
}

internal sealed class UnitOfWork(WarrantyDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await SaveAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw Conflict(ex);
        }
        catch (DbUpdateException ex) when (IsLostAppendRace(ex))
        {
            throw Conflict(ex);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> and saves its changes in one transaction; joins an already open
    /// transaction instead of nesting. The connection stays open for the transaction, so it carries
    /// the tenant set when it opened. A row changed since it was read (row version) surfaces as
    /// <see cref="ConcurrencyConflictException"/>.
    /// </summary>
    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        try
        {
            if (db.Database.CurrentTransaction is not null)
            {
                await work(ct);
                await SaveAsync(ct);
                return;
            }

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(
                async token =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(token);
                    await work(token);
                    await SaveAsync(token);
                    await transaction.CommitAsync(token);
                },
                ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw Conflict(ex);
        }
        catch (DbUpdateException ex) when (IsLostAppendRace(ex))
        {
            throw Conflict(ex);
        }
    }

    /// <summary>
    /// Saves; a notification whose dedupe key a concurrent unit of work queued first is dropped as "already
    /// queued" and the rest is saved again (in a transaction, EF Core rolled back to its savepoint).
    /// </summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && NotificationDeduplication.IsDedupeKeyViolation(ex))
            {
                if (!await NotificationDeduplication.DropAlreadyQueuedAsync(db, ct))
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Per-parent sequence keys of append-only histories (002 repair events and attempts, remedy decisions; 003 replacement
    /// events and units): two requests
    /// that read the same history append the same next <c>seq</c>, and the loser's insert fails before its row-version
    /// check would. That is the same lost race, so it is a concurrency conflict (409), not a server error.
    /// </summary>
    private static readonly HashSet<string> AppendSequenceIndexes = new(StringComparer.Ordinal)
    {
        "ix_repair_events_tenant_id_repair_id_seq",
        "ix_repair_attempts_tenant_id_repair_id_seq",
        "ix_remedy_decisions_tenant_id_claim_id_seq",
        "ix_replacement_events_tenant_id_replacement_id_seq",
        "ix_replacement_units_tenant_id_replacement_id_seq",
    };

    private static bool IsLostAppendRace(DbUpdateException exception)
        => exception is not DbUpdateConcurrencyException
           && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique
           && unique.ConstraintName is { } constraint
           && AppendSequenceIndexes.Contains(constraint);

    private static ConcurrencyConflictException Conflict(DbUpdateException ex)
        => new("A row was changed by another request since it was read; nothing was saved.", ex);
}
