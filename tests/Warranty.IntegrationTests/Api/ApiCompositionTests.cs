using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Warranty.Api.Auth;

namespace Warranty.IntegrationTests.Api;

/// <summary>
/// Boots the real API composition without any backing service: nothing here opens a database
/// connection, so it checks the pipeline (ProblemDetails, correlation header) and that every
/// registered service can be constructed.
/// </summary>
public sealed class ApiCompositionTests : IClassFixture<ApiCompositionTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiCompositionTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Unknown_routes_are_problem_details_carrying_the_correlation_id_header()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var correlationId = response.Headers.GetValues("X-Correlation-Id").ShouldHaveSingleItem();
        correlationId.ShouldNotBeNullOrWhiteSpace();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("status").GetInt32().ShouldBe(404);
        problem.GetProperty("correlationId").GetString().ShouldBe(correlationId);
    }

    [Fact]
    public async Task Health_endpoints_respond_in_development()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Every_registered_service_and_the_claimant_token_service_resolve()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ClaimantTokenService>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints.ShouldNotBeEmpty();
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // Unreachable on purpose: these tests must not need a database or blob store.
            builder.UseSetting("ConnectionStrings:warranty", "Host=127.0.0.1;Port=1;Database=warranty;Username=owner;Password=x");
            builder.UseSetting("ConnectionStrings:knowledge", "Host=127.0.0.1;Port=1;Database=knowledge;Username=owner;Password=x");
            builder.UseSetting("ConnectionStrings:blobs", "UseDevelopmentStorage=true");
            builder.UseSetting("Database:AppRolePassword", "test-only");

            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
        }
    }
}
