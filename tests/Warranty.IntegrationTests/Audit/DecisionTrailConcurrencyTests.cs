using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Infrastructure.Audit;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Audit;

/// <summary>
/// Concurrent writes to one claim's decision trail and one unit of work (FR-038, FR-039): appends from
/// parallel scopes are serialized per claim, so the trail keeps a gap-free <c>seq</c> and a valid hash
/// chain, and AI operations rows recorded from parallel model calls never corrupt the unit of work that
/// later saves a trail entry. The claim is a copy of a finalized seeded Aurora claim with its own serial
/// and reference (the trail is append-only, so the copy stays; being final, it is in no queue).
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class DecisionTrailConcurrencyTests(WarrantyAppFixture fixture)
{
    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_appends_for_one_claim_get_unique_contiguous_seq_and_a_valid_hash_chain()
    {
        var claimId = await CopyFinalizedClaimAsync();
        const int Writers = 16;
        const int AppendsPerWriter = 3;

        // Half the writers append in their own transaction per entry; the others append several entries in
        // one caller transaction, as the harness commits a step's entries with its checkpoint.
        using var start = new ManualResetEventSlim();
        var writers = Enumerable.Range(0, Writers).Select(writer => Task.Run(
            async () =>
            {
                start.Wait(Ct);
                using var tenant = TenantContextScope.Begin(Aurora, "aurora");
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var trail = scope.ServiceProvider.GetRequiredService<IDecisionTrailWriter>();
                if (writer % 2 == 0)
                {
                    for (var i = 0; i < AppendsPerWriter; i++)
                    {
                        await AppendAsync(trail, claimId, writer, i, Ct);
                    }
                }
                else
                {
                    await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
                        async token =>
                        {
                            for (var i = 0; i < AppendsPerWriter; i++)
                            {
                                await AppendAsync(trail, claimId, writer, i, token);
                            }
                        },
                        Ct);
                }
            },
            Ct)).ToList();
        start.Set();
        await Task.WhenAll(writers);

        const int Expected = Writers * AppendsPerWriter;
        (await TrailSeqsAsync(claimId)).ShouldBe(Enumerable.Range(1, Expected).ToList());
        (await VerifyAsync(claimId)).ShouldBe(HashChainVerification.Valid(Expected));
    }

    [Fact]
    public async Task Model_calls_recorded_from_parallel_calls_are_all_stored_with_the_next_trail_entry()
    {
        var claimId = await CopyFinalizedClaimAsync();
        var runId = Guid.CreateVersion7();
        const int Calls = 64;

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var aiOps = scope.ServiceProvider.GetRequiredService<IAiOpsRepository>();
            var trail = scope.ServiceProvider.GetRequiredService<IDecisionTrailWriter>();

            // Parallel photo analyses record their usage concurrently into the run's one unit of work.
            await Parallel.ForAsync(
                0,
                Calls,
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct },
                (_, _) =>
                {
                    aiOps.AddModelCall(ModelCallOf(claimId, runId));
                    return ValueTask.CompletedTask;
                });

            await trail.AppendAsync(claimId, TrailStep.EvidenceAnalyzed, "evidence", "Evidence analysed.", new { runId }, Ct);
            await trail.AppendAsync(claimId, TrailStep.RiskEvaluated, "system", "Risk evaluated.", new { runId }, Ct);
        }

        (await CountAsync("select count(*) from aiops.model_calls where run_id = @value", runId)).ShouldBe(Calls);
        (await TrailSeqsAsync(claimId)).ShouldBe([1, 2]);
        (await VerifyAsync(claimId)).ShouldBe(HashChainVerification.Valid(2));
    }

    private static Task AppendAsync(IDecisionTrailWriter trail, Guid claimId, int writer, int index, CancellationToken ct)
        => trail.AppendAsync(
            claimId, TrailStep.Correction, $"writer-{writer}", $"Concurrent append {index} of writer {writer}.", new { writer, index }, ct);

    private static ModelCall ModelCallOf(Guid claimId, Guid runId)
        => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = Aurora,
            ClaimId = claimId,
            RunId = runId,
            Agent = "evidence",
            Route = "evidence",
            Provider = "test",
            Model = "test-model",
            Status = ModelCallStatus.Ok,
            StartedAt = DateTimeOffset.UtcNow,
        };

    private async Task<HashChainVerification> VerifyAsync(Guid claimId)
    {
        using var tenant = TenantContextScope.Begin(Aurora, "aurora");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HashChainVerifier>().VerifyAsync(claimId, Ct);
    }

    /// <summary>A new claim with no trail: a copy of a finalized seeded Aurora claim with its own ID, reference and serial.</summary>
    private async Task<Guid> CopyFinalizedClaimAsync()
    {
        var claimId = Guid.CreateVersion7();
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            """
            create temp table claim_copy on commit drop as
                select * from claims.claims where tenant_id = @tenant and finalized_at is not null order by serial_number limit 1;
            update claim_copy set id = @id, reference = @reference, serial_number = @serial;
            insert into claims.claims select * from claim_copy;
            """,
            connection);
        command.Parameters.AddWithValue("tenant", Aurora);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("reference", ClaimReference.Generate());
        command.Parameters.AddWithValue("serial", $"TRAIL-{claimId:N}".ToUpperInvariant());
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        command.Transaction = transaction;
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
        await transaction.CommitAsync(Ct);
        return claimId;
    }

    private async Task<List<int>> TrailSeqsAsync(Guid claimId)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select seq from audit.decision_trail_entries where claim_id = @id order by seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var seqs = new List<int>();
        while (await reader.ReadAsync(Ct))
        {
            seqs.Add(reader.GetInt32(0));
        }

        return seqs;
    }

    private async Task<long> CountAsync(string sql, Guid value)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }
}
