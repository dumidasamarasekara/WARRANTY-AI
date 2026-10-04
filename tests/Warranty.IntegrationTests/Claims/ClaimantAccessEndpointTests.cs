using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Claims;

/// <summary>
/// Claimant access and status on the claimant channel (T058, FR-037, FR-037a): reference + submitted
/// contact → a claim-scoped token for the channel's tenant, the claimant view, and <c>GET
/// /api/public/tenant</c>. The claims are freshly submitted, so the view is checked in whatever status
/// adjudication has reached.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class ClaimantAccessEndpointTests(WarrantyAppFixture fixture)
{
    private static readonly string[] ViewProperties = ["reference", "status", "submittedAt", "productName", "outcomeExplanation", "requestedItems"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_reference_with_the_submitted_email_or_phone_opens_the_claimant_view()
    {
        var reference = await SubmitAsync();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var byEmail = await client.PostAsJsonAsync(
            "/api/public/claims/access", new { reference = reference.ToLowerInvariant(), contact = ContactEmail.ToUpperInvariant() }, Ct);
        using var byPhone = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = "1 (555) 010-9999" }, Ct);

        byEmail.StatusCode.ShouldBe(HttpStatusCode.OK);
        byPhone.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = await byEmail.Content.ReadFromJsonAsync<JsonElement>(Ct);
        token.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(1));

        using var withToken = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token.GetProperty("accessToken").GetString());
        using var response = await withToken.GetAsync($"/api/public/claims/{reference}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        view.GetProperty("reference").GetString().ShouldBe(reference);
        Enum.GetNames<ClaimStatus>().ShouldContain(view.GetProperty("status").GetString());
        view.GetProperty("productName").GetString().ShouldBe("Aurora Tab 10");
        view.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(ViewProperties);
    }

    [Fact]
    public async Task An_unknown_reference_and_a_wrong_contact_get_the_same_401_and_are_recorded_for_the_tenant()
    {
        var reference = await SubmitAsync();
        var unknown = ClaimReference.Generate();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var wrongContact = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = "someone.else@example.test" }, Ct);
        using var unknownReference = await client.PostAsJsonAsync("/api/public/claims/access", new { reference = unknown, contact = ContactEmail }, Ct);

        wrongContact.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownReference.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var first = await ProblemAsync(wrongContact);
        var second = await ProblemAsync(unknownReference);
        second.ShouldBe(first);
        wrongContact.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        (await AccessFailuresAsync(reference, TestStaffUsers.AuroraTenantId)).ShouldBe(1);
        (await AccessFailuresAsync(unknown, TestStaffUsers.AuroraTenantId)).ShouldBe(1);
    }

    [Fact]
    public async Task Another_tenants_channel_cannot_open_the_claim()
    {
        var reference = await SubmitAsync();
        using var borealis = fixture.CreateClaimantClient(WarrantyAppFixture.BorealisHost);

        using var response = await borealis.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = ContactEmail }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccessFailuresAsync(reference, TestStaffUsers.BorealisTenantId)).ShouldBe(1);
        (await AccessFailuresAsync(reference, TestStaffUsers.AuroraTenantId)).ShouldBe(0);
    }

    [Fact]
    public async Task The_claimant_view_needs_a_token_for_that_claim_on_its_own_tenant()
    {
        var reference = await SubmitAsync();
        var other = await SubmitAsync();
        var token = await AccessTokenAsync(reference);

        using var anonymous = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var noToken = await anonymous.GetAsync($"/api/public/claims/{reference}", Ct);
        noToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var aurora = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token);
        using var otherClaim = await aurora.GetAsync($"/api/public/claims/{other}", Ct);
        otherClaim.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var borealis = fixture.CreateClaimantClient(WarrantyAppFixture.BorealisHost, token);
        using var otherTenant = await borealis.GetAsync($"/api/public/claims/{reference}", Ct);
        otherTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var staff = fixture.CreateClient(WarrantyAppFixture.AuroraHost);
        staff.DefaultRequestHeaders.Authorization = new("Bearer", TestAuthHandler.TokenFor(TestStaffUsers.ReviewerAurora));
        using var staffToken = await staff.GetAsync($"/api/public/claims/{reference}", Ct);
        staffToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_public_tenant_info_is_the_channel_tenants_display_name()
    {
        var expected = await ScalarAsync<string>("select display_name from tenancy.tenants where id = @id", TestStaffUsers.AuroraTenantId);
        using var aurora = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var unknown = fixture.CreateClaimantClient("unknown.localhost");

        using var response = await aurora.GetAsync("/api/public/tenant", Ct);
        using var notFound = await unknown.GetAsync("/api/public/tenant", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        info.GetProperty("displayName").GetString().ShouldBe(expected);
        info.EnumerateObject().Select(p => p.Name).ShouldBe(["displayName"]);
        notFound.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Submits a fresh synthetic Aurora claim through the claimant channel and returns its reference.</summary>
    private async Task<string> SubmitAsync()
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsync("/api/public/claims", Submission(NewSerial()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
    }

    private async Task<string> AccessTokenAsync(string reference)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = ContactEmail }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }

    /// <summary>The problem without its per-request correlation ID.</summary>
    private static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return string.Join(
            "|",
            problem.EnumerateObject().Where(p => p.Name is not ("correlationId" or "traceId")).OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value.GetRawText()}"));
    }

    private async Task<long> AccessFailuresAsync(string target, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select count(*) from audit.security_events
            where kind = 'CLAIMANT_ACCESS_FAILED' and target = @target and tenant_id = @tenant
            """,
            connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<T> ScalarAsync<T>(string sql, object id)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
