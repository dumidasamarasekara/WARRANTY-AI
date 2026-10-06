extern alias apphost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Scenarios;

namespace Warranty.IntegrationTests.Smoke;

/// <summary>
/// The whole AppHost (PostgreSQL, Azurite, Keycloak, Ollama, migrations, API, SPA) started with the Aspire
/// testing builder on the configured container runtime (<c>DOTNET_ASPIRE_CONTAINER_RUNTIME=podman</c>), and
/// quickstart S1 submitted through the Aurora claimant channel until the system approves it (plan.md "Smoke").
/// AI calls are answered from recordings (<c>AiGateway:Mode=replay</c>): no Anthropic key is wired and no
/// provider is called. Slow (image pulls, model download, npm install) and bound to the AppHost's fixed
/// ports (Keycloak 8080, SPA 5173), so it is excluded from CI and the default run with
/// <c>--filter-not-trait "Category=Smoke"</c>; run it alone with <c>--filter-trait "Category=Smoke"</c>
/// while no <c>aspire run</c> is active.
/// </summary>
[Trait("Category", "Smoke")]
public sealed class AppHostSmokeTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan AdjudicationTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Containers whose data volumes the smoke run keeps: only Ollama's model cache, so the model is not downloaded every run.</summary>
    private static readonly HashSet<string> KeptVolumes = new(StringComparer.Ordinal) { "ollama" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_apphost_boots_in_replay_mode_and_S1_is_approved_by_the_system()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<apphost::Projects.Warranty_AppHost>(
            ["AiGateway:Mode=replay"], Ct);
        WithoutDataVolumes(builder);

        // Replay only: the API runs with the replay provider and the Anthropic key parameter is not even declared.
        builder.Resources.OfType<ParameterResource>().ShouldNotContain(p => p.Name == "anthropic-api-key");
        (await ApiEnvironmentAsync(builder, "AiGateway__Mode")).ShouldBe("replay");

        await using var app = await builder.BuildAsync(Ct);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        startup.CancelAfter(StartupTimeout);
        await app.StartAsync(startup.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", startup.Token);
        await app.ResourceNotifications.WaitForResourceAsync("web", KnownResourceStates.Running, startup.Token);

        var scenario = GoldenScenario.Load("S1");
        using var api = app.CreateHttpClient("api", "http");

        var reference = await SubmitAsync(api, scenario);
        var owner = await app.GetConnectionStringAsync("warranty", Ct)
                    ?? throw new InvalidOperationException("The 'warranty' database has no connection string.");
        var claimId = await ScalarAsync<Guid>(owner, "select id from claims.claims where reference = @value", reference, Ct);

        (await WaitUntilSettledAsync(owner, claimId)).ShouldBe(ClaimStatus.Approved);
        (await ScalarAsync<string>(owner, "select final_decided_by from claims.claims where id = @value", claimId, Ct))
            .ShouldBe(WireName.Of(DecidedBy.System));
        (await ScalarAsync<string>(
                owner, "select disposition from adjudication.adjudication_runs where claim_id = @value order by round desc limit 1", claimId, Ct))
            .ShouldBe(WireName.Of(Disposition.AutoApprove));
        (await ScalarAsync<long>(owner, "select count(*) from integration.repair_requests where claim_id = @value", claimId, Ct)).ShouldBe(1);

        // The claimant sees the approval through the channel.
        var view = await ClaimantViewAsync(api, scenario, reference);
        view.GetProperty("status").GetString().ShouldBe("Approved");
        view.GetProperty("outcomeExplanation").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Drops the data volumes (except <see cref="KeptVolumes"/>) so every run starts from an empty, freshly seeded
    /// database and blob store, and never touches the volumes (and their generated passwords) of a developer's <c>aspire run</c>.
    /// </summary>
    private static void WithoutDataVolumes(IDistributedApplicationTestingBuilder builder)
    {
        foreach (var resource in builder.Resources.Where(r => !KeptVolumes.Contains(r.Name)))
        {
            foreach (var volume in resource.Annotations.OfType<ContainerMountAnnotation>().Where(m => m.Type == ContainerMountType.Volume).ToList())
            {
                resource.Annotations.Remove(volume);
            }
        }
    }

    /// <summary>The value the AppHost gives the API for the environment variable <paramref name="name"/>, unresolved.</summary>
    private static async Task<object?> ApiEnvironmentAsync(IDistributedApplicationTestingBuilder builder, string name)
    {
        var api = builder.Resources.Single(r => r.Name == "api");
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, api, cancellationToken: Ct);
        foreach (var callback in api.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return context.EnvironmentVariables.GetValueOrDefault(name);
    }

    /// <summary>Submits through the claimant channel (tenant from the Host header) and returns the claim reference.</summary>
    private static async Task<string> SubmitAsync(HttpClient api, GoldenScenario scenario)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/claims") { Content = scenario.ToSubmission() };
        request.Headers.Host = scenario.ChannelHost;
        using var response = await api.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return accepted.GetProperty("reference").GetString()!;
    }

    /// <summary>Claimant access (reference + contact → claim token), then the claimant view of the claim.</summary>
    private static async Task<JsonElement> ClaimantViewAsync(HttpClient api, GoldenScenario scenario, string reference)
    {
        using var access = new HttpRequestMessage(HttpMethod.Post, "/api/public/claims/access")
        {
            Content = JsonContent.Create(new { reference, contact = scenario.ContactEmail }),
        };
        access.Headers.Host = scenario.ChannelHost;
        using var accessResponse = await api.SendAsync(access, Ct);
        accessResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await accessResponse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;

        using var view = new HttpRequestMessage(HttpMethod.Get, $"/api/public/claims/{reference}");
        view.Headers.Host = scenario.ChannelHost;
        view.Headers.Authorization = new("Bearer", token);
        using var viewResponse = await api.SendAsync(view, Ct);
        viewResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await viewResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Polls the claim's status (as the database owner) until it leaves Submitted/UnderEvaluation.</summary>
    private static async Task<ClaimStatus> WaitUntilSettledAsync(string owner, Guid claimId)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(AdjudicationTimeout);
        string? last = null;
        try
        {
            while (true)
            {
                last = await ScalarAsync<string>(owner, "select status from claims.claims where id = @value", claimId, timeout.Token);
                var status = WireName.Parse<ClaimStatus>(last);
                if (status is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation))
                {
                    return status;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Claim {claimId} did not settle within {AdjudicationTimeout}; last status: {last ?? "not found"}.");
        }
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, object value, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return await command.ExecuteScalarAsync(ct) is T result
            ? result
            : throw new ShouldAssertException($"'{sql}' returned no {typeof(T).Name}.");
    }
}
