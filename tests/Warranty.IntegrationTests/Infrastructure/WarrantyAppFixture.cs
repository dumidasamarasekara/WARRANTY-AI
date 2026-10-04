using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;
using Warranty.Api.Auth;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.IntegrationTests.Infrastructure;

/// <summary>
/// The API against real infrastructure: PostgreSQL with pgvector and Azurite in containers, migrated
/// and seeded by the migration service, AI calls answered from recordings (<c>AiGateway:Mode=replay</c>),
/// hash embeddings, and a test handler on the staff scheme in place of Keycloak. Shared by every test
/// class in <see cref="WarrantyAppCollection"/>; tests must not depend on each other's claims.
/// </summary>
public sealed class WarrantyAppFixture : IAsyncLifetime
{
    public const string AppRolePassword = "integration-app-password";

    public const string AuroraHost = "aurora.localhost";

    public const string BorealisHost = "borealis.localhost";

    /// <summary>The staff host (no claimant channel).</summary>
    public const string StaffHost = "localhost";

    private const string ClaimantSigningKey = "integration-tests-claimant-signing-key-0123456789";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    private WarrantyApiFactory? _factory;

    public WarrantyApiFactory Factory => _factory ?? throw new InvalidOperationException("The fixture is not initialized.");

    public string BlobConnectionString => _azurite.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(_postgres.StartAsync(ct), _azurite.StartAsync(ct));

        var exitCode = await MigrationRunner.RunAsync(
            OwnerConnectionString("warranty"), OwnerConnectionString("knowledge"), BlobConnectionString, AppRolePassword, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"The migration service failed with exit code {exitCode}.");
        }

        _factory = new WarrantyApiFactory(new Dictionary<string, string?>(MigrationRunner.HashEmbeddingRoute)
        {
            ["ConnectionStrings:warranty"] = OwnerConnectionString("warranty"),
            ["ConnectionStrings:knowledge"] = OwnerConnectionString("knowledge"),
            ["ConnectionStrings:blobs"] = BlobConnectionString,
            ["Database:AppRolePassword"] = AppRolePassword,
            ["AiGateway:Mode"] = "replay",
            ["ClaimantTokens:SigningKey"] = ClaimantSigningKey,
            ["ClaimJobWorker:PollInterval"] = "00:00:00.250",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        await _azurite.DisposeAsync();
    }

    /// <summary>The database owner (superuser in the container: not subject to row-level security).</summary>
    public string OwnerConnectionString(string database = "warranty")
        => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>The login the API uses: <c>warranty_app</c>, subject to row-level security.</summary>
    public string AppConnectionString(string database = "warranty")
        => new NpgsqlConnectionStringBuilder(OwnerConnectionString(database)) { Username = "warranty_app", Password = AppRolePassword }.ConnectionString;

    /// <summary>A client on the staff host signed in as the seeded user (see <see cref="TestStaffUsers"/>).</summary>
    public HttpClient CreateStaffClient(TestStaffUser user)
    {
        var client = CreateClient(StaffHost);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthHandler.TokenFor(user));
        return client;
    }

    public HttpClient CreateStaffClient(string username) => CreateStaffClient(TestStaffUsers.Find(username));

    /// <summary>A client on a claimant channel host (the API maps the Host header to a tenant), optionally with a claimant token.</summary>
    public HttpClient CreateClaimantClient(string channelHost, string? claimantToken = null)
    {
        var client = CreateClient(channelHost);
        if (claimantToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claimantToken);
        }

        return client;
    }

    /// <summary>A client without credentials whose requests carry <paramref name="host"/> as the Host header.</summary>
    public HttpClient CreateClient(string host)
        => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });

    /// <summary>A claim-scoped token as <c>POST /api/public/claims/access</c> would issue it (research R10).</summary>
    public string IssueClaimantToken(Guid tenantId, Guid claimId)
        => Factory.Services.GetRequiredService<ClaimantTokenService>().Issue(tenantId, claimId).AccessToken;

    /// <summary>The claim's current status, read as the database owner.</summary>
    public async Task<ClaimStatus?> ClaimStatusAsync(Guid claimId, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("select status from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        return await command.ExecuteScalarAsync(ct) is string status ? WireName.Parse<ClaimStatus>(status) : null;
    }

    /// <summary>Polls until the claim reaches one of <paramref name="expected"/>; throws with the last status seen on timeout.</summary>
    public Task<ClaimStatus> WaitForClaimStatusAsync(Guid claimId, params ClaimStatus[] expected)
        => WaitForClaimStatusAsync(claimId, expected.Contains, TimeSpan.FromSeconds(30));

    /// <summary>Polls until <paramref name="until"/> holds for the claim's status (e.g. "not transient").</summary>
    public async Task<ClaimStatus> WaitForClaimStatusAsync(Guid claimId, Func<ClaimStatus, bool> until, TimeSpan timeout)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutSource.CancelAfter(timeout);
        ClaimStatus? last = null;
        try
        {
            while (true)
            {
                last = await ClaimStatusAsync(claimId, timeoutSource.Token);
                if (last is { } status && until(status))
                {
                    return status;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeoutSource.Token);
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Claim {claimId} did not reach the expected status within {timeout}; last status: {last?.ToString() ?? "not found"}.");
        }
    }
}

/// <summary>The API host under test; replaces the Keycloak bearer handler with <see cref="TestAuthHandler"/>.</summary>
public sealed class WarrantyApiFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.PostConfigure<AuthenticationOptions>(options =>
            options.Schemes.Single(scheme => scheme.Name == JwtBearerDefaults.AuthenticationScheme).HandlerType = typeof(TestAuthHandler)));

        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
    }
}

[CollectionDefinition(Name)]
public sealed class WarrantyAppCollection : ICollectionFixture<WarrantyAppFixture>
{
    public const string Name = "warranty-app";
}
