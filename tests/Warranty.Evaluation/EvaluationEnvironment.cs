extern alias migration;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using migration::Warranty.MigrationService.Seeding;
using Npgsql;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;

namespace Warranty.Evaluation;

/// <summary>
/// The isolated evaluation environment (research R19): PostgreSQL with pgvector and Azurite in throwaway
/// containers, migrated and seeded by the real migration service exactly as production is, and the API's
/// composition root hosted in-process with the claim job worker turned off — the runner drives each
/// adjudication itself. AI calls follow <c>AiGateway:Mode</c> (replay from recordings, or live).
/// </summary>
internal sealed class EvaluationEnvironment : IAsyncDisposable
{
    private const string AppRolePassword = "evaluation-app-password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    private EvaluationApiFactory? _factory;

    public IServiceProvider Services => (_factory ?? throw new InvalidOperationException("The environment is not started.")).Services;

    /// <summary>The database owner (container superuser, not subject to row-level security).</summary>
    public string OwnerConnectionString(string database = "warranty")
        => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>Starts the containers, runs the migration service and starts the API host with <paramref name="aiSettings"/>.</summary>
    public async Task StartAsync(string seedPath, IReadOnlyDictionary<string, string?> aiSettings, CancellationToken ct)
    {
        await Task.WhenAll(_postgres.StartAsync(ct), _azurite.StartAsync(ct));
        var blobs = _azurite.GetConnectionString();
        var embeddings = aiSettings.Where(s => s.Key.StartsWith("AiGateway:Routes:embedding:", StringComparison.Ordinal)
                                               || s.Key == "ConnectionStrings:embeddings")
            .ToDictionary(s => s.Key, s => s.Value);

        var exitCode = await MigrateAsync(seedPath, blobs, embeddings, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"The migration service failed with exit code {exitCode}.");
        }

        var settings = new Dictionary<string, string?>(aiSettings)
        {
            ["ConnectionStrings:warranty"] = OwnerConnectionString("warranty"),
            ["ConnectionStrings:knowledge"] = OwnerConnectionString("knowledge"),
            ["ConnectionStrings:blobs"] = blobs,
            ["Database:AppRolePassword"] = AppRolePassword,
            ["ClaimantTokens:SigningKey"] = "evaluation-runner-claimant-signing-key-0123456789",
            ["ClaimJobWorker:Enabled"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Warranty"] = "Warning",
        };
        // WebApplicationFactory finds the API content root (appsettings.json: routes, pricing) through this variable outside test projects.
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_WARRANTY_API", Path.Combine(Path.GetDirectoryName(seedPath)!, "src", "Warranty.Api"));
        _factory = new EvaluationApiFactory(settings);
        _ = _factory.Services; // starts the host now, so a configuration error surfaces before the first case
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

    /// <summary>Runs the migration service composition (schema, roles, seed, knowledge index) once, as production does.</summary>
    private async Task<int> MigrateAsync(string seedPath, string blobs, IReadOnlyDictionary<string, string?> embeddings, CancellationToken ct)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:warranty"] = OwnerConnectionString("warranty"),
            ["ConnectionStrings:knowledge"] = OwnerConnectionString("knowledge"),
            ["ConnectionStrings:blobs"] = blobs,
            ["Database:AppRolePassword"] = AppRolePassword,
            ["AiGateway:Mode"] = "live",
            [MigrationServiceExtensions.SeedPathKey] = seedPath,
        });
        builder.Configuration.AddInMemoryCollection(embeddings);
        builder.AddMigrationService();

        using var host = builder.Build();
        Environment.ExitCode = -1;
        await host.RunAsync(ct);
        var exitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        return exitCode;
    }

    /// <summary>The API composition root (<c>Warranty.Api</c>'s Program) with the evaluation settings.</summary>
    private sealed class EvaluationApiFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
        }
    }
}
