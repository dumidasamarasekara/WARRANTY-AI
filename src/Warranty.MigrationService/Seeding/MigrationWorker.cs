namespace Warranty.MigrationService.Seeding;

/// <summary>
/// Runs the <see cref="SeedingPipeline"/> once and stops the host. The process exits with 0 only when
/// every step succeeded, so the AppHost starts the API only after a complete migration.
/// </summary>
internal sealed class MigrationWorker(SeedingPipeline pipeline, IHostApplicationLifetime lifetime, ILogger<MigrationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await pipeline.RunAsync(stoppingToken);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Migration and seeding failed");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
