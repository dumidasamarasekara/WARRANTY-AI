using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Warranty.AI.Gateway.Routing;
using Warranty.Api.Tenancy;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.IntegrationTests.Infrastructure;

/// <summary>Checks that the shared fixture is what the scenario tests assume it is.</summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class WarrantyAppFixtureTests(WarrantyAppFixture fixture)
{
    [Fact]
    public void The_api_answers_ai_calls_from_recordings_with_hash_embeddings()
    {
        var gateway = fixture.Factory.Services.GetRequiredService<IOptions<AiGatewayOptions>>().Value;

        gateway.Mode.ShouldBe(AiGatewayOptions.ReplayMode);
        gateway.Routes["embedding"].Provider.ShouldBe("hash");
    }

    [Theory]
    [MemberData(nameof(StaffUsers))]
    public async Task Every_seeded_staff_user_authenticates_with_the_claims_of_a_realm_token(string username)
    {
        var user = TestStaffUsers.Find(username);

        var result = await AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme, TestAuthHandler.TokenFor(user));

        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindFirst(TrustedClaimTypes.Subject)!.Value.ShouldBe(user.Subject);
        result.Principal.FindFirst(TrustedClaimTypes.TenantId)!.Value.ShouldBe(user.TenantId.ToString());
        result.Principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ShouldBe(user.Roles, ignoreOrder: true);
    }

    [Fact]
    public async Task Unknown_test_users_and_foreign_bearer_tokens_are_not_staff()
    {
        (await AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme, $"{TestAuthHandler.TokenPrefix}mallory"))
            .Succeeded.ShouldBeFalse();

        var claimantToken = fixture.IssueClaimantToken(TestStaffUsers.AuroraTenantId, Guid.CreateVersion7());
        (await AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme, claimantToken)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Issued_claimant_tokens_pass_the_claimant_scheme()
    {
        var claimId = Guid.CreateVersion7();

        var result = await AuthenticateAsync(
            TrustedClaimTypes.ClaimantScheme, fixture.IssueClaimantToken(TestStaffUsers.BorealisTenantId, claimId));

        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindFirst(TrustedClaimTypes.ClaimId)!.Value.ShouldBe(claimId.ToString());
        result.Principal.FindFirst(TrustedClaimTypes.TenantId)!.Value.ShouldBe(TestStaffUsers.BorealisTenantId.ToString());
    }

    [Fact]
    public async Task Staff_requests_pass_tenant_resolution()
    {
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerBorealis);

        // No endpoint lives here; tenant resolution refusing the user would answer 403 instead.
        using var response = await client.GetAsync("/api/fixture-probe", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Claimant_requests_on_an_unknown_host_are_refused_as_an_unknown_channel()
    {
        using var client = fixture.CreateClaimantClient("mallory.localhost");

        using var response = await client.GetAsync("/api/public/tenant", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await CountAsync($"select count(*) from audit.security_events where kind = '{WireName.Of(SecurityEventKind.UnknownChannel)}' and target = 'mallory.localhost'"))
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Both_tenants_are_seeded_and_the_app_login_sees_none_of_their_rows_without_a_tenant()
    {
        (await CountAsync("select count(*) from tenancy.tenants")).ShouldBe(2);
        (await CountAsync("select count(*) from catalog.product_serials")).ShouldBe(109);

        await using var app = new NpgsqlConnection(fixture.AppConnectionString());
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from catalog.product_serials", app);
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0);
    }

    [Fact]
    public async Task Waiting_for_a_claim_status_returns_once_it_is_reached_and_times_out_otherwise()
    {
        // A seeded history claim (finalized by the seed, marked seededHistory): claims submitted by other
        // tests share the claims table and may still be adjudicating or settle in review.
        var seededClaimId = await ScalarAsync<Guid>(
            "select c.id from claims.claims c where exists (select 1 from audit.decision_trail_entries t " +
            "where t.claim_id = c.id and (t.payload::jsonb ->> 'seededHistory') = 'true') order by c.serial_number limit 1");

        var status = await fixture.WaitForClaimStatusAsync(seededClaimId, ClaimStatus.Approved, ClaimStatus.Rejected);
        status.IsFinal().ShouldBeTrue();

        await Should.ThrowAsync<TimeoutException>(
            () => fixture.WaitForClaimStatusAsync(Guid.CreateVersion7(), _ => true, TimeSpan.FromMilliseconds(600)));
    }

    public static TheoryData<string> StaffUsers => new(TestStaffUsers.All.Select(user => user.Username));

    private async Task<AuthenticateResult> AuthenticateAsync(string scheme, string bearerToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        return await context.AuthenticateAsync(scheme);
    }

    private Task<long> CountAsync(string sql) => ScalarAsync<long>(sql);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
