using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Persistence of replacements and registered replacement serials through the Application ports (T310):
/// a replacement round-trips with its locations, return, proposals, units and events, both stay inside the
/// current tenant, and replacement events and registered serials are add-only. Claims are copies of a
/// finalized seeded Aurora claim; the aggregate's transitions arrive later, so its internal helpers are
/// invoked directly.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class ReplacementPersistenceTests(WarrantyAppFixture fixture)
{
    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;
    private static readonly Guid Borealis = TestStaffUsers.BorealisTenantId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_replacement_with_its_locations_return_proposals_units_and_events_round_trips()
    {
        var (claimId, _) = await CopyFinalizedClaimAsync();
        var versionId = await ScalarAsync<Guid>("select id from policy.policy_versions where tenant_id = @value order by id limit 1", Aurora);
        var productId = await AuroraProductIdAsync();
        var centres = await AuroraCentreIdsAsync();
        var at = DateTimeOffset.UtcNow;

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var replacements = scope.ServiceProvider.GetRequiredService<IReplacementRepository>();
            (await replacements.GetByClaimAsync(claimId, Ct)).ShouldBeNull();
            (await replacements.GetRowVersionAsync(claimId, Ct)).ShouldBeNull();

            replacements.Add(Replacement.Create(
                Guid.CreateVersion7(), Aurora, claimId, versionId, ReplacementProductRule.SameCategoryAtLeastClaimValue,
                ReplacementReturnRule.AtCollection, minimumCoverMonths: 6, ReplacementStatus.AwaitingUnit, centres[0], at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        uint firstVersion;
        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var replacements = scope.ServiceProvider.GetRequiredService<IReplacementRepository>();
            var replacement = (await replacements.GetByClaimAsync(claimId, Ct)).ShouldNotBeNull();
            replacement.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
            replacement.ReturnState.ShouldBe(ReturnState.NotYetReturned);
            replacement.MinimumCoverMonths.ShouldBe(6);
            replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(centres[0]);
            replacement.Return.ShouldBeNull();
            var suggested = replacement.Events.ShouldHaveSingleItem();
            suggested.Type.ShouldBe(ReplacementEventType.LocationSuggested);
            suggested.Id.ShouldBeGreaterThan(0);
            suggested.PayloadJson.ShouldContain(centres[0].ToString());
            firstVersion = (await replacements.GetRowVersionAsync(claimId, Ct)).ShouldNotBeNull();

            // A location change, a pending proposal, a reserved unit and the return in one save: the old
            // location is deactivated and the new one inserted together, which the partial unique index allows.
            Invoke(replacement, "AddLocation", centres[1], LocationAssignment.ChosenByStaff, "Closer to the customer.", "staff-sub", at);
            Invoke(replacement, "AddProposal", productId, "The claimed model is discontinued.", "staff-sub", at);
            Invoke(replacement, "AddUnit", centres[1], productId, " repl-unit-1 ", at);
            Invoke(replacement, "AddReturn", DateOnly.FromDateTime(at.UtcDateTime), centres[1], "SEEN-1", false, null, "Scratched.", "staff-sub", at);
            var reserved = (ReplacementEvent)Invoke(replacement, "AppendEvent",
                ReplacementEventType.UnitReserved, ReplacementStatus.AwaitingUnit, ReplacementStatus.ReadyForCollection,
                at, at, ReportSource.Phone, "staff-sub", "Unit set aside.", "{}", null)!;
            Invoke(replacement, "SetStatus", ReplacementStatus.ReadyForCollection, at);
            replacements.AddEvent(reserved);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

            // A saved event cannot be added again.
            Should.Throw<InvalidOperationException>(() => replacements.AddEvent(reserved));
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var replacements = scope.ServiceProvider.GetRequiredService<IReplacementRepository>();
            var replacement = (await replacements.GetByClaimAsync(claimId, Ct)).ShouldNotBeNull();
            replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
            replacement.Locations.Count.ShouldBe(2);
            replacement.Locations.Count(l => l.IsActive).ShouldBe(1);
            replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(centres[1]);
            replacement.PendingProposal.ShouldNotBeNull().ProductId.ShouldBe(productId);
            var unit = replacement.CurrentUnit.ShouldNotBeNull();
            unit.Seq.ShouldBe(1);
            unit.SerialNumber.ShouldBe("REPL-UNIT-1");
            unit.IsReserved.ShouldBeTrue();
            var faultyReturn = replacement.Return.ShouldNotBeNull();
            faultyReturn.ServiceCenterId.ShouldBe(centres[1]);
            faultyReturn.SerialMatches.ShouldBeFalse();
            replacement.Events.Select(e => (e.Seq, e.Type))
                .ShouldBe([(1, ReplacementEventType.LocationSuggested), (2, ReplacementEventType.UnitReserved)]);
            (await replacements.GetRowVersionAsync(claimId, Ct)).ShouldNotBe(firstVersion);
        }

        (await ScalarAsync<string>("select status from claims.replacements where claim_id = @value", claimId)).ShouldBe("ReadyForCollection");
        (await ScalarAsync<long>("select count(*) from audit.replacement_events where claim_id = @value", claimId)).ShouldBe(2);
    }

    [Fact]
    public async Task Serials_are_registered_found_by_serial_and_original_serial_and_reported_in_use()
    {
        var (claimId, claimSerial) = await CopyFinalizedClaimAsync();
        var productId = await AuroraProductIdAsync();
        var first = $"REPL-{Guid.CreateVersion7():N}".ToUpperInvariant();
        var second = $"REPL-{Guid.CreateVersion7():N}".ToUpperInvariant();
        var unused = $"REPL-{Guid.CreateVersion7():N}".ToUpperInvariant();
        var purchased = new DateOnly(2025, 3, 1);
        var handedOver = new DateOnly(2026, 10, 1);
        var coverEnd = RegisteredReplacementSerial.CoverEndOf(new DateOnly(2027, 3, 1), handedOver, 6);
        var at = DateTimeOffset.UtcNow;

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<IReplacementSerialRegistry>();
            (await registry.IsSerialInUseAsync(first, Ct)).ShouldBeFalse();

            var registration = RegisteredReplacementSerial.Register(
                Aurora, first.ToLowerInvariant(), productId, claimSerial, claimId, purchased, handedOver, coverEnd, at);
            registry.Register(registration);
            registry.Register(RegisteredReplacementSerial.Register(
                Aurora, second, productId, claimSerial, claimId, purchased, handedOver, coverEnd, at.AddMinutes(1)));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

            // A saved registration cannot be added again.
            Should.Throw<InvalidOperationException>(() => registry.Register(registration));
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<IReplacementSerialRegistry>();

            var found = (await registry.FindBySerialAsync($"  {first.ToLowerInvariant()} ", Ct)).ShouldNotBeNull();
            found.SerialNumber.ShouldBe(first);
            found.OriginalSerial.ShouldBe(claimSerial);
            found.ClaimId.ShouldBe(claimId);
            found.ProductId.ShouldBe(productId);
            found.OriginalPurchaseDate.ShouldBe(purchased);
            found.HandedOverOn.ShouldBe(handedOver);
            found.CoverEndDate.ShouldBe(new DateOnly(2027, 4, 1));
            (await registry.FindBySerialAsync(unused, Ct)).ShouldBeNull();

            (await registry.FindByOriginalSerialAsync(claimSerial.ToLowerInvariant(), Ct))
                .Select(s => s.SerialNumber).ShouldBe([first, second]);
            (await registry.FindByOriginalSerialAsync(first, Ct)).ShouldBeEmpty();

            (await registry.IsSerialInUseAsync(second.ToLowerInvariant(), Ct)).ShouldBeTrue();
            (await registry.IsSerialInUseAsync($" {claimSerial.ToLowerInvariant()} ", Ct)).ShouldBeTrue();
            (await registry.IsSerialInUseAsync(unused, Ct)).ShouldBeFalse();
        }

        (await ScalarAsync<long>("select count(*) from catalog.replacement_serials where claim_id = @value", claimId)).ShouldBe(2);
    }

    [Fact]
    public async Task Another_tenant_sees_neither_the_replacement_nor_the_registered_serials()
    {
        var (claimId, claimSerial) = await CopyFinalizedClaimAsync();
        var versionId = await ScalarAsync<Guid>("select id from policy.policy_versions where tenant_id = @value order by id limit 1", Aurora);
        var productId = await AuroraProductIdAsync();
        var unitSerial = $"REPL-{Guid.CreateVersion7():N}".ToUpperInvariant();
        var at = DateTimeOffset.UtcNow;

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IReplacementRepository>().Add(Replacement.Create(
                Guid.CreateVersion7(), Aurora, claimId, versionId, ReplacementProductRule.SameModel,
                ReplacementReturnRule.AtCollection, 0, ReplacementStatus.AwaitingUnit, suggestedServiceCenterId: null, at));
            scope.ServiceProvider.GetRequiredService<IReplacementSerialRegistry>().Register(RegisteredReplacementSerial.Register(
                Aurora, unitSerial, productId, claimSerial, claimId, new DateOnly(2025, 3, 1), new DateOnly(2026, 10, 1),
                new DateOnly(2027, 3, 1), at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (TenantContextScope.Begin(Borealis, "borealis"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var replacements = scope.ServiceProvider.GetRequiredService<IReplacementRepository>();
            (await replacements.GetByClaimAsync(claimId, Ct)).ShouldBeNull();
            (await replacements.GetRowVersionAsync(claimId, Ct)).ShouldBeNull();

            var registry = scope.ServiceProvider.GetRequiredService<IReplacementSerialRegistry>();
            (await registry.FindBySerialAsync(unitSerial, Ct)).ShouldBeNull();
            (await registry.FindByOriginalSerialAsync(claimSerial, Ct)).ShouldBeEmpty();
            (await registry.IsSerialInUseAsync(unitSerial, Ct)).ShouldBeFalse();
            (await registry.IsSerialInUseAsync(claimSerial, Ct)).ShouldBeFalse();
        }

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<IReplacementRepository>().GetByClaimAsync(claimId, Ct))
                .ShouldNotBeNull().ActiveLocation.ShouldBeNull();
            (await scope.ServiceProvider.GetRequiredService<IReplacementSerialRegistry>().IsSerialInUseAsync(unitSerial, Ct))
                .ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(typeof(IReplacementRepository))]
    [InlineData(typeof(IReplacementSerialRegistry))]
    public void The_ports_have_no_update_or_delete(Type port)
        => port.GetMethods()
            .Select(m => m.Name)
            .Where(name => name.StartsWith("Update", StringComparison.Ordinal)
                || name.StartsWith("Delete", StringComparison.Ordinal)
                || name.StartsWith("Remove", StringComparison.Ordinal))
            .ShouldBeEmpty();

    private static object? Invoke(Replacement replacement, string method, params object?[] args)
        => typeof(Replacement)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(replacement, args);

    private Task<Guid> AuroraProductIdAsync()
        => ScalarAsync<Guid>("select id from catalog.products where tenant_id = @value order by id limit 1", Aurora);

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
    private async Task<(Guid ClaimId, string Serial)> CopyFinalizedClaimAsync()
    {
        var claimId = Guid.CreateVersion7();
        var serial = $"REPLACE-{claimId:N}".ToUpperInvariant();
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
        command.Parameters.AddWithValue("serial", serial);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        command.Transaction = transaction;
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
        await transaction.CommitAsync(Ct);
        return (claimId, serial);
    }

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
