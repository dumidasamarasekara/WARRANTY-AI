extern alias migration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using migration::Warranty.MigrationService.Seeding;

namespace Warranty.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real migration service composition (schema, roles, seed, knowledge index) against test
/// containers, with hash embeddings instead of Ollama.
/// </summary>
internal static class MigrationRunner
{
    /// <summary>The embedding route shared by the migration service and the API under test.</summary>
    public static readonly IReadOnlyDictionary<string, string?> HashEmbeddingRoute = new Dictionary<string, string?>
    {
        ["AiGateway:Routes:embedding:Provider"] = "hash",
        ["AiGateway:Routes:embedding:Model"] = "hash-embedding",
        ["AiGateway:Routes:embedding:Dimensions"] = "768",
    };

    /// <returns>The process exit code the migration service set (0 when every step succeeded).</returns>
    public static async Task<int> RunAsync(
        string warrantyOwner, string knowledgeOwner, string blobs, string appRolePassword, CancellationToken ct)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:warranty"] = warrantyOwner,
            ["ConnectionStrings:knowledge"] = knowledgeOwner,
            ["ConnectionStrings:blobs"] = blobs,
            ["Database:AppRolePassword"] = appRolePassword,
            ["AiGateway:Mode"] = "live",
            [MigrationServiceExtensions.SeedPathKey] = Path.Combine(RepositoryRoot(), "seed"),
        });
        builder.Configuration.AddInMemoryCollection(HashEmbeddingRoute);
        builder.AddMigrationService();

        using var host = builder.Build();
        Environment.ExitCode = -1;
        await host.RunAsync(ct);
        return Environment.ExitCode;
    }

    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Warranty.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Warranty.slnx) not found.");
    }
}
