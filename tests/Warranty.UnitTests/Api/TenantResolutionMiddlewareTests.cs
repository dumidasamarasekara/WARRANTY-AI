using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Warranty.Api.Tenancy;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.Domain.Tenancy;
using Warranty.Infrastructure.Audit;

namespace Warranty.UnitTests.Api;

public sealed class TenantResolutionMiddlewareTests
{
    private static readonly Tenant Aurora = Tenant.Create(
        Guid.Parse("11111111-1111-7111-8111-111111111111"), "aurora", "Aurora Electronics", DateTimeOffset.UnixEpoch);

    private static readonly Tenant Borealis = Tenant.Create(
        Guid.Parse("22222222-2222-7222-8222-222222222222"), "borealis", "Borealis Devices", DateTimeOffset.UnixEpoch);

    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly ISecurityEventWriter _events = Substitute.For<ISecurityEventWriter>();

    public TenantResolutionMiddlewareTests()
    {
        _tenants.FindByChannelHostAsync("aurora.localhost", Arg.Any<CancellationToken>()).Returns(Aurora);
        _tenants.FindByChannelHostAsync("borealis.localhost", Arg.Any<CancellationToken>()).Returns(Borealis);
        _tenants.GetActiveTenantAsync(Aurora.Id, Arg.Any<CancellationToken>()).Returns(Aurora);
        _tenants.GetActiveTenantAsync(Borealis.Id, Arg.Any<CancellationToken>()).Returns(Borealis);
    }

    [Fact]
    public async Task Claimant_channel_resolves_the_tenant_from_the_host_ignoring_case_port_query_and_headers()
    {
        using var provider = Services();

        var result = await SendAsync(provider, "/api/public/claims", "Aurora.LocalHost:5173", configure: request =>
        {
            request.QueryString = new QueryString($"?tenantId={Borealis.Id}");
            request.Headers["X-Tenant-Id"] = Borealis.Id.ToString();
        });

        result.NextCalled.ShouldBeTrue();
        result.Tenant.TenantId.ShouldBe(Aurora.Id);
        result.Tenant.TenantSlug.ShouldBe("aurora");
        result.Tenant.KnowledgeNamespace.ShouldBe("tenant-aurora");
        result.Tenant.PrincipalId.ShouldBe("claimant");
        result.Tenant.Roles.ShouldBeEmpty();
        result.Tenant.IsSystem.ShouldBeFalse();
        result.Tenant.CorrelationId.ShouldNotBeNullOrWhiteSpace();
        await _tenants.Received(1).FindByChannelHostAsync("aurora.localhost", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Claimant_channel_ignores_a_staff_token_of_another_tenant()
    {
        using var provider = Services();

        var result = await SendAsync(provider, "/api/public/tenant", "aurora.localhost", Staff(Borealis.Id, "claims-agent"));

        result.NextCalled.ShouldBeTrue();
        result.Tenant.TenantId.ShouldBe(Aurora.Id);
        result.Tenant.PrincipalId.ShouldBe("claimant");
    }

    [Fact]
    public async Task Unknown_channel_is_404_problem_details_and_an_operator_only_event()
    {
        using var provider = Services();

        var result = await SendAsync(provider, "/api/public/claims", "unknown.localhost");

        result.NextCalled.ShouldBeFalse();
        result.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        result.ContentType.ShouldStartWith("application/problem+json");
        result.Tenant.IsResolved.ShouldBeFalse();
        await _events.Received(1).RecordOperatorEventAsync(
            SecurityEventKind.UnknownChannel, "anonymous", "unknown.localhost", Arg.Any<object?>(), Arg.Any<CancellationToken>());
        await _events.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Channel_lookups_are_cached_but_unknown_hosts_are_looked_up_every_time()
    {
        using var provider = Services();

        await SendAsync(provider, "/api/public/tenant", "aurora.localhost");
        await SendAsync(provider, "/api/public/tenant", "aurora.localhost:5173");
        await SendAsync(provider, "/api/public/tenant", "unknown.localhost");
        await SendAsync(provider, "/api/public/tenant", "unknown.localhost");

        await _tenants.Received(1).FindByChannelHostAsync("aurora.localhost", Arg.Any<CancellationToken>());
        await _tenants.Received(2).FindByChannelHostAsync("unknown.localhost", Arg.Any<CancellationToken>());
        await _events.Received(2).RecordOperatorEventAsync(
            SecurityEventKind.UnknownChannel, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_claimant_token_of_the_channel_tenant_becomes_the_claimant_principal()
    {
        using var provider = Services(claimantScheme: true);
        var claimId = Guid.NewGuid();

        var result = await SendAsync(provider, $"/api/public/claims/WC-1", "aurora.localhost",
            configure: request => request.Headers[TestClaimantHandler.Header] = $"{Aurora.Id}|{claimId}");

        result.NextCalled.ShouldBeTrue();
        result.Tenant.TenantId.ShouldBe(Aurora.Id);
        result.Tenant.PrincipalId.ShouldBe($"claimant:{claimId}");
        result.Tenant.PrincipalName.ShouldBe("claimant");
    }

    [Fact]
    public async Task A_claimant_token_of_another_tenant_is_404_and_a_cross_tenant_event()
    {
        using var provider = Services(claimantScheme: true);
        var claimId = Guid.NewGuid();

        var result = await SendAsync(provider, "/api/public/claims/WC-1", "aurora.localhost",
            configure: request => request.Headers[TestClaimantHandler.Header] = $"{Borealis.Id}|{claimId}");

        result.NextCalled.ShouldBeFalse();
        result.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        result.Tenant.IsResolved.ShouldBeFalse();
        await _events.Received(1).RecordOperatorEventAsync(
            SecurityEventKind.CrossTenantAccessDenied, $"claimant:{claimId}", "aurora.localhost", Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Staff_requests_resolve_the_tenant_subject_name_and_roles_from_the_token()
    {
        using var provider = Services();

        var result = await SendAsync(provider, "/api/claims", "localhost:7443", Staff(Aurora.Id, "claims-agent", "claims-reviewer"));

        result.NextCalled.ShouldBeTrue();
        result.Tenant.TenantId.ShouldBe(Aurora.Id);
        result.Tenant.KnowledgeNamespace.ShouldBe("tenant-aurora");
        result.Tenant.PrincipalId.ShouldBe("staff-sub-1");
        result.Tenant.PrincipalName.ShouldBe("agent-reviewer.aurora");
        result.Tenant.Roles.ShouldBe(["claims-agent", "claims-reviewer"], ignoreOrder: true);
        result.Tenant.IsSystem.ShouldBeFalse();
    }

    [Fact]
    public async Task Staff_requests_never_take_the_tenant_from_the_host_or_query()
    {
        using var provider = Services();

        var result = await SendAsync(provider, "/api/claims", "aurora.localhost", Staff(Borealis.Id, "auditor"),
            request => request.QueryString = new QueryString($"?tenantId={Aurora.Id}"));

        result.Tenant.TenantId.ShouldBe(Borealis.Id);
        await _tenants.DidNotReceiveWithAnyArgs().FindByChannelHostAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("33333333-3333-7333-8333-333333333333")]
    public async Task Staff_tokens_without_one_active_tenant_are_403(string? tenantClaim)
    {
        using var provider = Services();
        var claims = new List<Claim> { new("sub", "staff-sub-1") };
        if (tenantClaim is not null)
        {
            claims.Add(new Claim("tenant_id", tenantClaim));
        }

        var result = await SendAsync(provider, "/api/claims", "localhost", new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")));

        result.NextCalled.ShouldBeFalse();
        result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        result.Tenant.IsResolved.ShouldBeFalse();
    }

    [Fact]
    public async Task Staff_tokens_with_two_different_tenants_are_403()
    {
        using var provider = Services();
        var identity = new ClaimsIdentity(
            [new("sub", "staff-sub-1"), new("tenant_id", Aurora.Id.ToString()), new("tenant_id", Borealis.Id.ToString())], "Bearer");

        var result = await SendAsync(provider, "/api/claims", "localhost", new ClaimsPrincipal(identity));

        result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        result.NextCalled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("/api/claims")]
    [InlineData("/health")]
    [InlineData("/openapi/v1.json")]
    public async Task Unauthenticated_and_non_api_requests_pass_through_unresolved(string path)
    {
        using var provider = Services();

        var result = await SendAsync(provider, path, "aurora.localhost");

        result.NextCalled.ShouldBeTrue();
        result.Tenant.IsResolved.ShouldBeFalse();
        await _tenants.DidNotReceiveWithAnyArgs().FindByChannelHostAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_claimant_principal_on_a_staff_path_resolves_no_tenant()
    {
        using var provider = Services();
        var claimant = new ClaimsPrincipal(new ClaimsIdentity(
            [new("tenant_id", Aurora.Id.ToString()), new("claim_id", Guid.NewGuid().ToString()), new("scope", "claimant")], "Claimant"));

        var result = await SendAsync(provider, "/api/claims", "aurora.localhost", claimant);

        result.NextCalled.ShouldBeTrue();
        result.Tenant.IsResolved.ShouldBeFalse();
    }

    [Fact]
    public void The_api_registers_its_tenant_context_and_the_request_source_for_security_events()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestSourceAccessor>(Substitute.For<IRequestSourceAccessor>());
        services.AddTenantResolution();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITenantContext>().ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<HttpTenantContext>());
        provider.GetServices<IRequestSourceAccessor>().ShouldHaveSingleItem().ShouldBeOfType<HttpRequestSourceAccessor>();
    }

    private ServiceProvider Services(bool claimantScheme = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantResolution();
        services.AddSingleton(_tenants);
        services.AddSingleton(_events);
        if (claimantScheme)
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestClaimantHandler>(TrustedClaimTypes.ClaimantScheme, null);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<Result> SendAsync(
        ServiceProvider provider, string path, string host, ClaimsPrincipal? user = null, Action<HttpRequest>? configure = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        context.Response.Body = new MemoryStream();
        if (user is not null)
        {
            context.User = user;
        }

        configure?.Invoke(context.Request);

        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<TenantResolutionMiddleware>.Instance);
        var tenant = scope.ServiceProvider.GetRequiredService<HttpTenantContext>();
        await middleware.InvokeAsync(context, tenant, scope.ServiceProvider.GetRequiredService<TenantDirectory>());
        return new Result(nextCalled, context.Response.StatusCode, context.Response.ContentType, tenant);
    }

    private static ClaimsPrincipal Staff(Guid tenantId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "staff-sub-1"),
            new("preferred_username", "agent-reviewer.aurora"),
            new("tenant_id", tenantId.ToString()),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private sealed record Result(bool NextCalled, int StatusCode, string? ContentType, HttpTenantContext Tenant);

    /// <summary>Stands in for the claimant token scheme: header value <c>{tenantId}|{claimId}</c>.</summary>
    private sealed class TestClaimantHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Header = "Test-Claimant";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var value))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var parts = value.ToString().Split('|');
            var identity = new ClaimsIdentity(
                [new("tenant_id", parts[0]), new("claim_id", parts[1]), new("scope", "claimant")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
