using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Claims;

/// <summary>
/// <see cref="SupplementClaim"/> against PostgreSQL, row-level security and the blob store (T100): the
/// next round's evidence, job and <c>SupplementReceived</c> entry are saved with the claim's new round,
/// earlier rounds stay untouched, and a claim that is no longer pending or belongs to another tenant is
/// refused. The HTTP routes are T101 and the full US5 scenarios T098; here the claim is put into
/// <c>PendingInformation</c> directly once its first round has settled.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class SupplementClaimPersistenceTests(WarrantyAppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_supplement_saves_the_next_round_with_its_evidence_job_and_trail_entry()
    {
        var (claimId, reference) = await PendingClaimAsync();
        var invoice = Pdf();
        var photo = Jpeg();

        var result = await SupplementAsync(
            TestStaffUsers.AuroraTenantId, "aurora",
            new SupplementClaimCommand(ClaimChannel.ClaimantPortal, claimId, reference, "Here is the invoice.", [Upload(invoice)], [Upload(photo)]));

        var accepted = result.ShouldBeOfType<SupplementClaimResult.Accepted>();
        accepted.Round.ShouldBe(2);
        accepted.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(2);

        var evidence = await RowsAsync(
            "select round, kind, blob_path from claims.claim_evidence where claim_id = @id order by round", claimId,
            r => (Round: r.GetInt32(0), Kind: r.GetString(1), BlobPath: r.GetString(2)));
        evidence.Count(e => e.Round == 1).ShouldBe(2, "the first round's evidence stays");
        var round2 = evidence.Where(e => e.Round == 2).ToList();
        round2.Select(e => e.Kind).ShouldBe(["Invoice", "Photo"], ignoreOrder: true);
        var container = new BlobServiceClient(fixture.BlobConnectionString).GetBlobContainerClient("tenant-aurora");
        foreach (var item in round2)
        {
            item.BlobPath.ShouldStartWith($"claims/{claimId}/2/");
            (await container.GetBlobClient(item.BlobPath).ExistsAsync(Ct)).Value.ShouldBeTrue(item.BlobPath);
        }

        var jobs = await RowsAsync(
            "select round, tenant_id from claims.claim_jobs where claim_id = @id order by round", claimId,
            r => (Round: r.GetInt32(0), TenantId: r.GetGuid(1)));
        jobs.Select(j => j.Round).ShouldBe([1, 2]);
        jobs.ShouldAllBe(j => j.TenantId == TestStaffUsers.AuroraTenantId);

        var supplement = (await RowsAsync(
                "select actor, tenant_id, payload::text from audit.decision_trail_entries where claim_id = @id and step = 'SupplementReceived'", claimId,
                r => (Actor: r.GetString(0), TenantId: r.GetGuid(1), Payload: r.GetString(2))))
            .ShouldHaveSingleItem();
        supplement.Actor.ShouldBe(Claim.ClaimantSubmitter);
        supplement.TenantId.ShouldBe(TestStaffUsers.AuroraTenantId);
        using var payload = JsonDocument.Parse(supplement.Payload);
        payload.RootElement.GetProperty("round").GetInt32().ShouldBe(2);
        payload.RootElement.GetProperty("note").GetString().ShouldBe("Here is the invoice.");
        payload.RootElement.GetProperty("evidence").GetArrayLength().ShouldBe(2);

        // The claim is no longer waiting for information: a second supplement is refused.
        var again = await SupplementAsync(
            TestStaffUsers.AuroraTenantId, "aurora",
            new SupplementClaimCommand(ClaimChannel.ClaimantPortal, claimId, reference, "Once more.", [], []));
        again.ShouldBeOfType<SupplementClaimResult.Conflict>();
    }

    [Fact]
    public async Task A_claim_of_another_tenant_is_not_found_and_nothing_is_stored()
    {
        var (claimId, reference) = await PendingClaimAsync();

        var result = await SupplementAsync(
            TestStaffUsers.BorealisTenantId, "borealis",
            new SupplementClaimCommand(ClaimChannel.ClaimantPortal, claimId, reference, null, [], [Upload(Jpeg())]));

        result.ShouldBeOfType<SupplementClaimResult.NotFound>();
        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(1);
        (await ScalarAsync<long>("select count(*) from claims.claim_evidence where claim_id = @id and round > 1", claimId)).ShouldBe(0);
    }

    /// <summary>Submits an Aurora claim, waits for its first round to settle and puts it into <c>PendingInformation</c>.</summary>
    private async Task<(Guid ClaimId, string Reference)> PendingClaimAsync()
    {
        var serial = NewSerial();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsync("/api/public/claims", Submission(serial), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
        var claimId = await ScalarAsync<Guid>("select id from claims.claims where serial_number = @id", serial);

        await fixture.WaitForClaimStatusAsync(
            claimId, s => s is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation), TimeSpan.FromSeconds(60));
        await WaitForJobsDoneAsync(claimId);

        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            update claims.claims
               set status = 'PendingInformation', final_outcome = null, final_decided_by = null,
                   final_explanation = null, finalized_at = null
             where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", claimId);
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        return (claimId, reference);
    }

    private async Task WaitForJobsDoneAsync(Guid claimId)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await ScalarAsync<long>("select count(*) from claims.claim_jobs where claim_id = @id and status not in ('Done', 'Failed')", claimId) == 0)
            {
                return;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException($"The jobs of claim {claimId} did not finish.");
    }

    private async Task<SupplementClaimResult> SupplementAsync(Guid tenantId, string tenantSlug, SupplementClaimCommand command)
    {
        using var tenant = TenantContextScope.Begin(tenantId, tenantSlug, Claim.ClaimantSubmitter, $"it-supplement-{Guid.NewGuid():N}"[..32]);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SupplementClaim>().ExecuteAsync(command, Ct);
    }

    private static EvidenceUpload Upload(EvidenceFile file)
        => new(file.Name, file.Bytes.Length, () => new MemoryStream(file.Bytes, writable: false));

    private async Task<T> ScalarAsync<T>(string sql, object id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Reads as the database owner (no row-level security), for assertions only.</summary>
    private async Task<List<T>> RowsAsync<T>(string sql, object id, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }
}
