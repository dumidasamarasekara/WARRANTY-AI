using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// The replacement in the staff claim detail and the eligible replacement locations (003 T328): <c>GET /api/claims/{id}</c>
/// carries <c>replacement</c> (ReplacementView) with its own ETag, its history and the events allowed for the caller's role
/// (none for an auditor), only when the claim has a replacement; another tenant sees neither. The claim is a copy of a
/// finalized seeded Aurora claim with a replacement written through the repository (the executor creates it in T324).
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class ReplacementDetailTests(WarrantyAppFixture fixture)
{
    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;

    /// <summary>Seeded Aurora Service Center Springfield (NA, issues replacements, priority 10).</summary>
    private static readonly Guid Springfield = Guid.Parse("11111111-0003-7000-8000-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_claim_detail_carries_the_replacement_with_the_events_allowed_for_the_callers_role()
    {
        var claimId = await CopyFinalizedClaimAsync();
        (await DetailAsync(TestStaffUsers.AgentAurora, claimId)).TryGetProperty("replacement", out _).ShouldBeFalse();

        await AddReplacementAsync(claimId);

        var agent = (await DetailAsync(TestStaffUsers.AgentAurora, claimId)).GetProperty("replacement");
        agent.GetProperty("status").GetString().ShouldBe("AwaitingUnit");
        agent.GetProperty("productRule").GetString().ShouldBe("SameCategoryAtLeastClaimValue");
        agent.GetProperty("returnRule").GetString().ShouldBe("AtCollection");
        agent.GetProperty("returnState").GetString().ShouldBe("NotYetReturned");
        agent.GetProperty("etag").GetString().ShouldNotBeNull().ShouldMatch("^\"[0-9]+\"$");
        var location = agent.GetProperty("activeLocation");
        location.GetProperty("id").GetGuid().ShouldBe(Springfield);
        location.GetProperty("suggested").GetBoolean().ShouldBeTrue();
        var suggested = agent.GetProperty("events").EnumerateArray().ShouldHaveSingleItem();
        suggested.GetProperty("type").GetString().ShouldBe("LocationSuggested");
        suggested.GetProperty("actor").GetString().ShouldBe("system");
        var agentEvents = Strings(agent.GetProperty("allowedEvents"));
        agentEvents.ShouldContain("UnitReserved");
        agentEvents.ShouldContain("ModelProposed");
        agentEvents.ShouldNotContain("ModelApproved");

        var auditor = (await DetailAsync(TestStaffUsers.AuditorAurora, claimId)).GetProperty("replacement");
        auditor.GetProperty("allowedEvents").GetArrayLength().ShouldBe(0);
        auditor.GetProperty("etag").GetString().ShouldBe(agent.GetProperty("etag").GetString());

        var reviewer = (await DetailAsync(TestStaffUsers.ReviewerAurora, claimId)).GetProperty("replacement");
        Strings(reviewer.GetProperty("allowedEvents")).ShouldBe(agentEvents);

        using var other = fixture.CreateStaffClient(TestStaffUsers.AgentBorealis);
        using var response = await other.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_eligible_locations_are_the_tenants_replacement_centres_serving_the_claim()
    {
        var claimId = await CopyFinalizedClaimAsync();
        var region = await ScalarAsync<string>("select region from claims.claims where id = @value", claimId);

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var locations = (await scope.ServiceProvider.GetRequiredService<ReplacementQueries>().GetReplacementLocationsAsync(claimId, Ct))
                .ShouldNotBeNull();

            locations.ShouldNotBeEmpty();
            locations[0].Suggested.ShouldBeTrue();
            locations.Count(l => l.Suggested).ShouldBe(1);
            locations.ShouldAllBe(l => l.ServesClaim && l.Region.ToString() == region);
            (await ScalarAsync<long>(
                $"select count(*) from integration.service_centers where id = any(@value) and (tenant_id <> '{Aurora}' or not issues_replacements)",
                locations.Select(l => l.Id).ToArray())).ShouldBe(0);
        }

        using (TenantContextScope.Begin(TestStaffUsers.BorealisTenantId, "borealis"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<ReplacementQueries>().GetReplacementLocationsAsync(claimId, Ct)).ShouldBeNull();
        }
    }

    private static List<string?> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToList();

    private async Task AddReplacementAsync(Guid claimId)
    {
        var versionId = await ScalarAsync<Guid>("select id from policy.policy_versions where tenant_id = @value order by id limit 1", Aurora);
        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IReplacementRepository>().Add(Replacement.Create(
                Guid.CreateVersion7(), Aurora, claimId, versionId, ReplacementProductRule.SameCategoryAtLeastClaimValue,
                ReplacementReturnRule.AtCollection, 0, ReplacementStatus.AwaitingUnit, Springfield, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }
    }

    private async Task<JsonElement> DetailAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>A new claim: a copy of a finalized seeded Aurora NA claim with its own ID, reference and serial.</summary>
    private async Task<Guid> CopyFinalizedClaimAsync()
    {
        var claimId = Guid.CreateVersion7();
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            """
            create temp table claim_copy on commit drop as
                select * from claims.claims
                where tenant_id = @tenant and finalized_at is not null and region = 'NA' and product_id is not null
                order by serial_number limit 1;
            update claim_copy set id = @id, reference = @reference, serial_number = @serial;
            insert into claims.claims select * from claim_copy;
            """,
            connection);
        command.Parameters.AddWithValue("tenant", Aurora);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("reference", ClaimReference.Generate());
        command.Parameters.AddWithValue("serial", $"REPLACE-{claimId:N}".ToUpperInvariant());
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        command.Transaction = transaction;
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
        await transaction.CommitAsync(Ct);
        return claimId;
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
