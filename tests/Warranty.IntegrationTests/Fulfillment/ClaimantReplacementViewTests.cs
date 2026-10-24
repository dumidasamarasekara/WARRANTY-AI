using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// The claimant status endpoint for a Replacement remedy (003 T330; FR-032 – FR-034): <c>GET /api/public/claims/{reference}</c>
/// shows the replacement label, the active location's public details, the replacement timeline and, once a unit is
/// reserved, its model name, and never the unit's serial number, notes or the location's contact. Until the executor
/// creates replacements (T324) the claim is a copy of a finalized seeded Aurora claim and its remedy and replacement
/// are added through the Application ports, as in <see cref="ReplacementPersistenceTests"/>.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class ClaimantReplacementViewTests(WarrantyAppFixture fixture)
{
    private const string ContactEmail = "replacement.claimant@example.test";
    private const string StaffNote = "Unit set aside behind the counter; staff-only note.";

    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_claimant_sees_the_replacement_location_timeline_and_reserved_model_but_no_staff_only_data()
    {
        var (claimId, reference) = await CopyFinalizedClaimAsync();
        var versionId = await ScalarAsync<Guid>("select id from policy.policy_versions where tenant_id = @value order by id limit 1", Aurora);
        var centreId = await ScalarAsync<Guid>("select id from integration.service_centers where tenant_id = @value order by id limit 1", Aurora);
        var productId = await ScalarAsync<Guid>("select id from catalog.products where tenant_id = @value order by id limit 1", Aurora);
        var centreName = await ScalarAsync<string>("select name from integration.service_centers where id = @value", centreId);
        var centreContact = await ScalarAsync<string?>("select contact from integration.service_centers where id = @value", centreId);
        var modelName = await ScalarAsync<string>("select name from catalog.products where id = @value", productId);
        var serial = $"REPL-{Guid.CreateVersion7():N}".ToUpperInvariant();
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IRemedyRepository>().Add(ClaimRemedy.SetByPolicy(
                Guid.CreateVersion7(), Aurora, claimId, versionId, RemedyScenario.CoveredDefect, RemedyType.Replacement,
                [RemedyType.Replacement], at));
            scope.ServiceProvider.GetRequiredService<IReplacementRepository>().Add(Replacement.Create(
                Guid.CreateVersion7(), Aurora, claimId, versionId, ReplacementProductRule.SameModel,
                ReplacementReturnRule.AtCollection, minimumCoverMonths: 0, ReplacementStatus.AwaitingUnit, centreId, at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        var awaiting = (await ClaimantViewAsync(reference)).GetProperty("remedy");
        awaiting.GetProperty("remedy").GetString().ShouldBe("Replacement");
        awaiting.GetProperty("label").GetString().ShouldBe("We are preparing your replacement");
        awaiting.GetProperty("completed").GetBoolean().ShouldBeFalse();
        awaiting.GetProperty("location").GetProperty("name").GetString().ShouldBe(centreName);
        awaiting.TryGetProperty("replacementModelName", out _).ShouldBeFalse();
        awaiting.TryGetProperty("repairCentre", out _).ShouldBeFalse();
        awaiting.GetProperty("steps").GetArrayLength().ShouldBe(0);
        Steps(awaiting).ShouldBe([("Ready for collection", "current"), ("Handed to you", "upcoming")]);

        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var replacements = scope.ServiceProvider.GetRequiredService<IReplacementRepository>();
            var replacement = (await replacements.GetByClaimAsync(claimId, Ct)).ShouldNotBeNull();
            var reserved = replacement.ReserveUnit(
                productId, serial, new ReplacementReport(at.AddMinutes(1), ReportSource.Phone, "staff-sub", StaffRole.ClaimsAgent, StaffNote, at.AddMinutes(1)));
            replacements.AddEvent(reserved);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        var claimant = await ClaimantViewAsync(reference);
        var ready = claimant.GetProperty("remedy");
        ready.GetProperty("label").GetString().ShouldBe("Your replacement is ready for collection");
        ready.GetProperty("replacementModelName").GetString().ShouldBe(modelName);
        ready.GetProperty("location").GetProperty("name").GetString().ShouldBe(centreName);
        Steps(ready).ShouldBe([("Ready for collection", "current"), ("Handed to you", "upcoming")]);

        var text = claimant.GetRawText();
        text.ShouldNotContain(serial, Case.Insensitive);
        text.ShouldNotContain(StaffNote);
        text.ShouldNotContain("staff-sub");
        text.ShouldNotContain("returnState", Case.Insensitive);
        text.ShouldNotContain("returnRule", Case.Insensitive);
        if (!string.IsNullOrWhiteSpace(centreContact))
        {
            text.ShouldNotContain(centreContact);
        }
    }

    private static List<(string Title, string State)> Steps(JsonElement remedy)
        => remedy.GetProperty("replacementSteps").EnumerateArray()
            .Select(s => (s.GetProperty("title").GetString()!, s.GetProperty("state").GetString()!))
            .ToList();

    /// <summary>The claimant view on the Aurora channel, opened with the reference and the claim's email.</summary>
    private async Task<JsonElement> ClaimantViewAsync(string reference)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var access = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = ContactEmail }, Ct);
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await access.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();

        using var withToken = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token);
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>A new claim: a copy of a finalized seeded Aurora claim with its own ID, reference, serial and email.</summary>
    private async Task<(Guid ClaimId, string Reference)> CopyFinalizedClaimAsync()
    {
        var claimId = Guid.CreateVersion7();
        var reference = ClaimReference.Generate();
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            """
            create temp table claim_copy on commit drop as
                select * from claims.claims where tenant_id = @tenant and finalized_at is not null order by serial_number limit 1;
            update claim_copy set id = @id, reference = @reference, serial_number = @serial, contact_email = @email;
            insert into claims.claims select * from claim_copy;
            """,
            connection);
        command.Parameters.AddWithValue("tenant", Aurora);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("reference", reference);
        command.Parameters.AddWithValue("serial", $"REPLACE-{claimId:N}".ToUpperInvariant());
        command.Parameters.AddWithValue("email", ContactEmail);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        command.Transaction = transaction;
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
        await transaction.CommitAsync(Ct);
        return (claimId, reference);
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
