using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Reference;

/// <summary>
/// <c>GET /api/policies</c> (T081): the seeded policy versions of the staff user's tenant with effective
/// dates and regions, for every staff role, and nothing of the other tenant.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class PolicyEndpointTests(WarrantyAppFixture fixture)
{
    private static readonly string[] SummaryProperties = ["policyCode", "title", "version", "effectiveFrom", "effectiveTo", "regions"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("agent.aurora")]
    [InlineData("reviewer.aurora")]
    [InlineData("auditor.aurora")]
    public async Task Every_staff_role_sees_its_tenants_policy_versions_with_dates_and_regions(string username)
    {
        using var client = fixture.CreateStaffClient(username);

        var versions = await ListAsync(client);

        versions.Select(v => (v.GetProperty("policyCode").GetString(), v.GetProperty("version").GetInt32()))
            .ShouldBe([("AUR-WP", 1), ("AUR-WP", 2)]);
        versions[0].EnumerateObject().Select(p => p.Name).ShouldBe(SummaryProperties, ignoreOrder: true);
        versions[0].GetProperty("title").GetString().ShouldBe("Aurora Limited Warranty");
        versions[0].GetProperty("effectiveFrom").GetString().ShouldBe("2025-01-01");
        versions[0].GetProperty("effectiveTo").GetString().ShouldBe("2026-06-30");
        versions[1].GetProperty("effectiveFrom").GetString().ShouldBe("2026-07-01");
        versions[1].GetProperty("effectiveTo").ValueKind.ShouldBe(JsonValueKind.Null);
        versions[1].GetProperty("regions").EnumerateArray().Select(r => r.GetString()).ShouldBe(["NA", "EU"]);
    }

    [Fact]
    public async Task Another_tenants_staff_sees_only_its_own_policies()
    {
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentBorealis);

        var versions = await ListAsync(client);

        versions.Select(v => v.GetProperty("policyCode").GetString()).ShouldBe(["BOR-WP"]);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        using var client = fixture.CreateClient(WarrantyAppFixture.StaffHost);

        using var response = await client.GetAsync("/api/policies", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/policies", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. body.EnumerateArray()];
    }
}
