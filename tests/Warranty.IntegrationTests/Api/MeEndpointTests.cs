using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Api;

/// <summary>
/// <c>GET /api/me</c> (T080): the staff user, their tenant's display name and currency, and their
/// staff roles — all from the token's tenant, never from the request.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class MeEndpointTests(WarrantyAppFixture fixture)
{
    private static readonly string[] MeProperties = ["sub", "name", "tenantDisplayName", "tenantCurrency", "roles"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> StaffUsers => new(TestStaffUsers.All.Select(user => user.Username));

    [Theory]
    [MemberData(nameof(StaffUsers))]
    public async Task Every_staff_user_gets_their_tenant_currency_and_roles(string username)
    {
        var user = TestStaffUsers.Find(username);
        var (displayName, currency) = await TenantAsync(user.TenantId);
        using var client = fixture.CreateStaffClient(user);

        using var response = await client.GetAsync("/api/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        me.EnumerateObject().Select(p => p.Name).ShouldBe(MeProperties, ignoreOrder: true);
        me.GetProperty("sub").GetString().ShouldBe(user.Subject);
        me.GetProperty("name").GetString().ShouldBe(user.Username);
        me.GetProperty("tenantDisplayName").GetString().ShouldBe(displayName);
        me.GetProperty("tenantCurrency").GetString().ShouldBe(currency);
        me.GetProperty("tenantCurrency").GetString()!.ShouldMatch("^[A-Z]{3}$");
        me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(user.Roles);
    }

    [Fact]
    public async Task A_tenant_header_or_query_does_not_change_the_tenant()
    {
        var (aurora, _) = await TenantAsync(TestStaffUsers.AuroraTenantId);
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestStaffUsers.BorealisTenantId.ToString());

        using var response = await client.GetAsync($"/api/me?tenantId={TestStaffUsers.BorealisTenantId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("tenantDisplayName").GetString().ShouldBe(aurora);
    }

    [Fact]
    public async Task Anonymous_and_claimant_callers_are_refused()
    {
        using var anonymous = fixture.CreateClient(WarrantyAppFixture.StaffHost);
        using var noToken = await anonymous.GetAsync("/api/me", Ct);
        noToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var claimant = fixture.CreateClient(WarrantyAppFixture.StaffHost);
        claimant.DefaultRequestHeaders.Authorization =
            new("Bearer", fixture.IssueClaimantToken(TestStaffUsers.AuroraTenantId, Guid.CreateVersion7()));
        using var claimantToken = await claimant.GetAsync("/api/me", Ct);
        claimantToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<(string DisplayName, string Currency)> TenantAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select t.display_name, s.currency from tenancy.tenants t
            join tenancy.tenant_settings s on s.tenant_id = t.id
            where t.id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return (reader.GetString(0), reader.GetString(1).Trim());
    }
}
