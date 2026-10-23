using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Persistence of remedies, repairs and de-duplicated notifications through the Application ports (T212):
/// a dedupe key is queued at most once per tenant, also when a concurrent unit of work wins the race
/// (research R7), and each aggregate round-trips. Claims are copies of a finalized seeded Aurora claim.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class FulfillmentPersistenceTests(WarrantyAppFixture fixture)
{
    private const string Template = "RepairReadyForCollection";
    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_dedupe_key_is_queued_once_and_reported_as_duplicate_afterwards()
    {
        var claimId = await CopyFinalizedClaimAsync();
        var key = $"repair-ready:{Guid.CreateVersion7()}";

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var first = await notifications.EnqueueAsync(claimId, Template, key, Ct);
            var second = await notifications.EnqueueAsync(claimId, Template, key, Ct);
            first.AlreadyQueued.ShouldBeFalse();
            first.NotificationId.ShouldNotBeNull();
            second.ShouldBe(NotificationEnqueueResult.Duplicate);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var again = await scope.ServiceProvider.GetRequiredService<INotificationService>().EnqueueAsync(claimId, Template, key, Ct);
            again.ShouldBe(NotificationEnqueueResult.Duplicate);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        (await CountNotificationsAsync(key)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_unit_of_work_that_loses_the_dedupe_race_drops_its_duplicate_and_saves_the_rest(bool inTransaction)
    {
        var claimId = await CopyFinalizedClaimAsync();
        var key = $"repair-ready:{Guid.CreateVersion7()}";

        using var tenant = TenantContextScope.Begin(Aurora, "aurora");
        await using var loser = fixture.Factory.Services.CreateAsyncScope();
        var loserNotifications = loser.ServiceProvider.GetRequiredService<INotificationService>();

        // The loser checks first (nothing queued yet) ...
        (await loserNotifications.EnqueueAsync(claimId, Template, key, Ct)).AlreadyQueued.ShouldBeFalse();
        var otherId = await loserNotifications.EnqueueAsync(claimId, "ClaimApproved", Ct);

        // ... then the winner queues and saves the same key.
        await using (var winner = fixture.Factory.Services.CreateAsyncScope())
        {
            (await winner.ServiceProvider.GetRequiredService<INotificationService>().EnqueueAsync(claimId, Template, key, Ct))
                .AlreadyQueued.ShouldBeFalse();
            await winner.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        var unitOfWork = loser.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (inTransaction)
        {
            await unitOfWork.ExecuteInTransactionAsync(_ => Task.CompletedTask, Ct);
        }
        else
        {
            await unitOfWork.SaveChangesAsync(Ct);
        }

        (await CountNotificationsAsync(key)).ShouldBe(1);
        (await ScalarAsync<long>("select count(*) from integration.notifications where id = @value", otherId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_remedy_its_decisions_and_its_completion_round_trip()
    {
        var claimId = await CopyFinalizedClaimAsync();
        var versionId = await ScalarAsync<Guid>("select id from policy.policy_versions where tenant_id = @value order by id limit 1", Aurora);
        var at = DateTimeOffset.UtcNow;
        var allowed = new[] { RemedyType.Refund, RemedyType.Replacement };

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedies = scope.ServiceProvider.GetRequiredService<IRemedyRepository>();
            var remedy = ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), Aurora, claimId, versionId, RemedyScenario.RepairNotEconomical, allowed,
                RemedyPendingReason.RepairNotEconomical, at);
            remedies.Add(remedy);
            remedies.AddDecision(RemedyDecision.Evaluated(
                Guid.CreateVersion7(), Aurora, claimId, 1, RemedyScenario.RepairNotEconomical, versionId, "repairNotEconomical",
                allowed, failedAttempts: 0, estimateAmount: 412.50m, at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedies = scope.ServiceProvider.GetRequiredService<IRemedyRepository>();
            var remedy = (await remedies.GetAsync(claimId, Ct)).ShouldNotBeNull();
            remedy.State.ShouldBe(RemedyState.AwaitingDecision);
            remedy.PendingReason.ShouldBe(RemedyPendingReason.RepairNotEconomical);
            remedy.AllowedRemedies.ShouldBe(allowed);
            remedy.Remedy.ShouldBeNull();

            remedy.Decide(RemedyType.Refund, at);
            remedies.AddDecision(RemedyDecision.Decided(
                Guid.CreateVersion7(), Aurora, claimId, 2, RemedyScenario.RepairNotEconomical, versionId, "repairNotEconomical",
                allowed, RemedyType.Refund, RemedyDecidedBy.Reviewer, "reviewer-sub", "Customer prefers a refund.", 0, 412.50m, at));
            var completion = RemedyCompletion.Record(
                Guid.CreateVersion7(), remedy, DateOnly.FromDateTime(at.UtcDateTime), "RF-0001", Guid.CreateVersion7(), "staff-sub", at);
            remedies.AddCompletion(completion);
            remedy.Complete(RemedyType.Refund, at);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedies = scope.ServiceProvider.GetRequiredService<IRemedyRepository>();
            var remedy = (await remedies.GetAsync(claimId, Ct)).ShouldNotBeNull();
            remedy.State.ShouldBe(RemedyState.Completed);
            remedy.Remedy.ShouldBe(RemedyType.Refund);
            remedy.PendingReason.ShouldBeNull();

            var decisions = await remedies.GetDecisionsAsync(claimId, Ct);
            decisions.Select(d => (d.Seq, d.Kind)).ShouldBe([(1, RemedyDecisionKind.Evaluated), (2, RemedyDecisionKind.Decided)]);
            decisions[1].DecidedBy.ShouldBe(RemedyDecidedBy.Reviewer);
            decisions[1].EstimateAmount.ShouldBe(412.50m);
            decisions[1].AllowedRemedies.ShouldBe(allowed);

            var completion = (await remedies.GetCompletionAsync(claimId, Ct)).ShouldNotBeNull();
            completion.Remedy.ShouldBe(RemedyType.Refund);
            completion.Reference.ShouldBe("RF-0001");
        }

        (await ScalarAsync<string>("select pending_reason from claims.claim_remedies where claim_id = @value", claimId)).ShouldBeNull();
        (await ScalarAsync<string>("select state from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("Completed");
    }

    [Fact]
    public async Task A_repair_with_its_hand_overs_and_events_round_trips_and_keeps_one_active_hand_over()
    {
        var claimId = await CopyFinalizedClaimAsync();
        var centres = await AuroraCentreIdsAsync();
        var at = DateTimeOffset.UtcNow;

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IRepairRepository>()
                .Add(Repair.Create(Guid.CreateVersion7(), Aurora, claimId, centres[0], at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repair = (await scope.ServiceProvider.GetRequiredService<IRepairRepository>().GetByClaimAsync(claimId, Ct)).ShouldNotBeNull();
            repair.Status.ShouldBe(RepairStatus.AwaitingHandover);
            repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(centres[0]);
            var suggested = repair.Events.ShouldHaveSingleItem();
            suggested.Type.ShouldBe(RepairEventType.CentreSuggested);
            suggested.Id.ShouldBeGreaterThan(0);
            suggested.PayloadJson.ShouldContain(centres[0].ToString());

            // A centre change (domain transitions arrive later): the old hand-over is deactivated and the new
            // one inserted in the same save, which the partial unique index must allow.
            AddHandover(repair, centres[1], HandoverAssignment.ChosenByStaff, "Closer to the customer.", "staff-sub", at);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repair = (await scope.ServiceProvider.GetRequiredService<IRepairRepository>().GetByClaimAsync(claimId, Ct)).ShouldNotBeNull();
            repair.Handovers.Count.ShouldBe(2);
            repair.Handovers.Count(h => h.IsActive).ShouldBe(1);
            repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(centres[1]);
        }
    }

    private static void AddHandover(
        Repair repair, Guid centreId, HandoverAssignment assignment, string reason, string recordedBy, DateTimeOffset at)
        => typeof(Repair)
            .GetMethod("AddHandover", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repair, [centreId, assignment, reason, recordedBy, at]);

    private async Task<List<Guid>> AuroraCentreIdsAsync()
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select id from integration.service_centers where tenant_id = @tenant order by id limit 2", connection);
        command.Parameters.AddWithValue("tenant", Aurora);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        ids.Count.ShouldBe(2);
        return ids;
    }

    /// <summary>A new claim: a copy of a finalized seeded Aurora claim with its own ID, reference and serial.</summary>
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
        command.Parameters.AddWithValue("serial", $"FULFIL-{claimId:N}".ToUpperInvariant());
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        command.Transaction = transaction;
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
        await transaction.CommitAsync(Ct);
        return claimId;
    }

    private Task<long> CountNotificationsAsync(string key)
        => ScalarAsync<long>("select count(*) from integration.notifications where dedupe_key = @value", key);

    private async Task<T> ScalarAsync<T>(string sql, object value)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is DBNull or null ? default! : (T)result;
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }
}
