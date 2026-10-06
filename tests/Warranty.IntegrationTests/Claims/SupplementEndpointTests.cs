using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Claims;

/// <summary>
/// <c>POST /api/public/claims/{reference}/supplements</c> (claimant token) and
/// <c>POST /api/claims/{claimId}/supplements</c> (claims agent) (T101, FR-010): a supplement to a
/// <c>PendingInformation</c> claim is 202 and starts the next round; a claim that is not pending is 409;
/// a reference that is not the token's claim, an unknown ID and another tenant's claim are 404; an
/// empty or oversized supplement is 400; no claimant token is 401 and a staff user without the
/// claims-agent role is 403. The claim is put into <c>PendingInformation</c> directly once its first
/// round has settled; the full US5 scenarios are T098.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class SupplementEndpointTests(WarrantyAppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Claimant route ---------------------------------------------------------------------------

    [Fact]
    public async Task A_claimant_supplements_a_pending_claim_and_gets_202_without_internal_ids()
    {
        var (claimId, reference) = await PendingClaimAsync();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, await ClaimantTokenAsync(reference));

        using var response = await client.PostAsync($"/api/public/claims/{reference}/supplements", Supplement("Here is the invoice.", Pdf()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location?.ToString().ShouldBe($"/api/public/claims/{reference}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.TryGetProperty("claimId", out _).ShouldBeFalse();
        body.GetProperty("reference").GetString().ShouldBe(reference);
        body.GetProperty("status").GetString().ShouldBe("UnderEvaluation");
        body.GetProperty("round").GetInt32().ShouldBe(2);
        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(2);
        (await ScalarAsync<long>("select count(*) from claims.claim_evidence where claim_id = @id and round = 2", claimId)).ShouldBe(1);
        (await ScalarAsync<string>(
                "select actor from audit.decision_trail_entries where claim_id = @id and step = 'SupplementReceived'", claimId))
            .ShouldBe(Claim.ClaimantSubmitter);
    }

    [Fact]
    public async Task An_empty_or_oversized_supplement_is_400_and_stores_nothing()
    {
        var (claimId, reference) = await PendingClaimAsync();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, await ClaimantTokenAsync(reference));

        using var empty = await client.PostAsync($"/api/public/claims/{reference}/supplements", Supplement(null), Ct);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorsAsync(empty)).ShouldContainKey(SupplementClaim.SupplementKey);

        using var longNote = await client.PostAsync(
            $"/api/public/claims/{reference}/supplements", Supplement(new string('x', SupplementClaim.MaxNoteLength + 1)), Ct);
        longNote.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorsAsync(longNote)).ShouldContainKey(SupplementClaim.NoteKey);

        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(1);
        (await ScalarAsync<string>("select status from claims.claims where id = @id", claimId)).ShouldBe("PendingInformation");
    }

    [Fact]
    public async Task A_claim_that_is_not_pending_information_is_409_on_the_claimant_route()
    {
        var reference = await SubmitAsClaimantAsync();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, await ClaimantTokenAsync(reference));

        using var response = await client.PostAsync($"/api/public/claims/{reference}/supplements", Supplement("More details."), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString().ShouldBe(SupplementClaim.NotPendingDetail);
    }

    [Fact]
    public async Task A_reference_that_is_not_the_token_claim_is_404()
    {
        var (claimId, reference) = await PendingClaimAsync();
        var other = await SubmitAsClaimantAsync();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, await ClaimantTokenAsync(other));

        using var response = await client.PostAsync($"/api/public/claims/{reference}/supplements", Supplement("Not my claim."), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(claimId.ToString(), Case.Insensitive);
        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_claimant_token_of_aurora_on_the_borealis_channel_supplements_nothing()
    {
        var (claimId, reference) = await PendingClaimAsync();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.BorealisHost, await ClaimantTokenAsync(reference));

        using var response = await client.PostAsync($"/api/public/claims/{reference}/supplements", Supplement("Wrong channel."), Ct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(1);
    }

    [Fact]
    public async Task The_claimant_route_without_a_claimant_token_is_401()
    {
        var reference = await SubmitAsClaimantAsync();
        using var anonymous = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var response = await anonymous.PostAsync($"/api/public/claims/{reference}/supplements", Supplement("Anonymous."), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- Staff route ------------------------------------------------------------------------------

    [Fact]
    public async Task A_claims_agent_supplements_a_pending_claim_and_gets_202_with_the_claim_id()
    {
        var (claimId, reference) = await PendingClaimAsync();
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);

        using var response = await client.PostAsync($"/api/claims/{claimId}/supplements", Supplement("Invoice received by phone.", Pdf(), Jpeg()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location?.ToString().ShouldBe($"/api/claims/{claimId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("claimId").GetGuid().ShouldBe(claimId);
        body.GetProperty("reference").GetString().ShouldBe(reference);
        body.GetProperty("status").GetString().ShouldBe("UnderEvaluation");
        body.GetProperty("round").GetInt32().ShouldBe(2);
        (await ScalarAsync<long>("select count(*) from claims.claim_evidence where claim_id = @id and round = 2", claimId)).ShouldBe(2);
        (await ScalarAsync<string>(
                "select actor from audit.decision_trail_entries where claim_id = @id and step = 'SupplementReceived'", claimId))
            .ShouldBe(TestStaffUsers.AgentAurora.Subject);
    }

    [Fact]
    public async Task A_claim_that_is_not_pending_information_is_409_on_the_staff_route()
    {
        var reference = await SubmitAsClaimantAsync();
        var claimId = await ScalarAsync<Guid>("select id from claims.claims where reference = @id", reference);
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);

        using var response = await client.PostAsync($"/api/claims/{claimId}/supplements", Supplement("More details."), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task An_unknown_claim_and_another_tenant_claim_are_404_on_the_staff_route()
    {
        var (claimId, _) = await PendingClaimAsync();
        using var borealis = fixture.CreateStaffClient(TestStaffUsers.AgentBorealis);
        using var aurora = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);

        using var foreign = await borealis.PostAsync($"/api/claims/{claimId}/supplements", Supplement("Cross-tenant probe."), Ct);
        using var unknown = await aurora.PostAsync($"/api/claims/{Guid.CreateVersion7()}/supplements", Supplement("Unknown claim."), Ct);

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(1);
    }

    [Fact]
    public async Task Staff_without_the_claims_agent_role_get_403()
    {
        var (claimId, _) = await PendingClaimAsync();

        foreach (var user in new[] { TestStaffUsers.ReviewerAurora, TestStaffUsers.AuditorAurora })
        {
            using var client = fixture.CreateStaffClient(user);
            using var response = await client.PostAsync($"/api/claims/{claimId}/supplements", Supplement("Not allowed."), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, user.Username);
        }

        (await ScalarAsync<int>("select current_round from claims.claims where id = @id", claimId)).ShouldBe(1);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>A <c>SupplementForm</c> with an optional note, PDFs as <c>invoice</c> and images as <c>photos</c>.</summary>
    private static MultipartFormDataContent Supplement(string? note, params EvidenceFile[] files)
    {
        var form = new MultipartFormDataContent();
        if (note is not null)
        {
            form.Add(new StringContent(note), "note");
        }

        foreach (var file in files)
        {
            form.Add(file.Content(), file.ContentType == "application/pdf" ? "invoice" : "photos", file.Name);
        }

        if (note is null && files.Length == 0)
        {
            // An empty multipart body is still a form; a blank note counts as no note.
            form.Add(new StringContent(" "), "note");
        }

        return form;
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;

    private async Task<string> SubmitAsClaimantAsync()
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsync("/api/public/claims", Submission(NewSerial()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
    }

    private async Task<string> ClaimantTokenAsync(string reference)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = ContactEmail }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }

    /// <summary>Submits an Aurora claim, waits for its first round to settle and puts it into <c>PendingInformation</c>.</summary>
    private async Task<(Guid ClaimId, string Reference)> PendingClaimAsync()
    {
        var reference = await SubmitAsClaimantAsync();
        var claimId = await ScalarAsync<Guid>("select id from claims.claims where reference = @id", reference);

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

    /// <summary>Reads as the database owner (no row-level security), for assertions only.</summary>
    private async Task<T> ScalarAsync<T>(string sql, object id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }
}
