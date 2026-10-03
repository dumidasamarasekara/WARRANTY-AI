extern alias apphost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Warranty.IntegrationTests.AppHost;

/// <summary>Checks the AppHost's application model (resources, images, start order) without starting anything.</summary>
public sealed class AppHostModelTests
{
    [Fact]
    public async Task Every_resource_is_declared_with_its_start_dependencies()
    {
        await using var app = await Model("replay");
        var resources = app.Resources.ToDictionary(r => r.Name);

        new[] { "postgres", "warranty", "knowledge", "storage", "blobs", "keycloak", "ollama", "embeddings", "migrations", "api", "web" }
            .ShouldBeSubsetOf(resources.Keys);

        var image = resources["postgres"].Annotations.OfType<ContainerImageAnnotation>().Single();
        (image.Image, image.Tag).ShouldBe(("pgvector/pgvector", "pg17"));

        Waits(resources["migrations"]).ShouldBe(
            ["embeddings:WaitUntilHealthy", "ollama:WaitUntilHealthy", "postgres:WaitUntilHealthy", "storage:WaitUntilHealthy"]);
        Waits(resources["api"]).ShouldContain("migrations:WaitForCompletion");
        Waits(resources["web"]).ShouldContain("api:WaitUntilHealthy");
    }

    [Fact]
    public async Task The_spa_listens_on_the_port_bound_to_the_claimant_channels()
    {
        await using var app = await Model("replay");

        var endpoint = app.Resources.Single(r => r.Name == "web").Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "http");
        (endpoint.Port, endpoint.IsProxied).ShouldBe((5173, false));
    }

    [Theory]
    [InlineData("live", true)]
    [InlineData("replay", false)]
    public async Task The_anthropic_key_is_a_secret_parameter_only_in_live_mode(string mode, bool expected)
    {
        await using var app = await Model(mode);

        var key = app.Resources.OfType<ParameterResource>().SingleOrDefault(p => p.Name == "anthropic-api-key");
        (key is not null).ShouldBe(expected);
        if (key is not null)
        {
            key.Secret.ShouldBeTrue();
        }
    }

    private static async Task<IDistributedApplicationTestingBuilder> Model(string aiMode)
        => await DistributedApplicationTestingBuilder.CreateAsync<apphost::Projects.Warranty_AppHost>(
            [$"AiGateway:Mode={aiMode}"], TestContext.Current.CancellationToken);

    /// <summary>The resource's distinct start dependencies as <c>name:waitType</c>, ordered by name.</summary>
    private static List<string> Waits(IResource resource)
        => resource.Annotations.OfType<WaitAnnotation>().Select(w => $"{w.Resource.Name}:{w.WaitType}").Distinct().Order(StringComparer.Ordinal).ToList();
}
