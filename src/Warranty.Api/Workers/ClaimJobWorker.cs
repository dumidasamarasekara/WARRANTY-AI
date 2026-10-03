using System.Diagnostics;
using Microsoft.Extensions.Options;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Adjudication;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;

namespace Warranty.Api.Workers;

/// <summary>Configuration of the in-process claim job worker (section <c>ClaimJobWorker</c>).</summary>
public sealed class ClaimJobWorkerOptions
{
    public const string SectionName = "ClaimJobWorker";

    public bool Enabled { get; set; } = true;

    /// <summary>Wait between polls when the queue is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Wait after a polling error (e.g. the database is unavailable).</summary>
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Lease of a dequeued job; another worker may take it over once it expires.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Runs queued adjudication jobs (research R12). For each job it opens a DI scope and a
/// <see cref="TenantContextScope"/> for the job's tenant as the <c>adjudication-service</c> principal —
/// the job record is the worker's only tenant source (research R9) — and calls
/// <see cref="IAdjudicationRunner"/>. AI failures are outcomes the runner handles (the claim goes to
/// human review); an exception escaping the runner is an infrastructure failure and the job is
/// retried with backoff up to its maximum attempts. Completing or failing the job uses a fresh scope,
/// so it never saves changes the runner left pending.
/// </summary>
internal sealed class ClaimJobWorker(
    IServiceScopeFactory scopes, IOptions<ClaimJobWorkerOptions> options, TimeProvider time, ILogger<ClaimJobWorker> logger)
    : BackgroundService
{
    internal static readonly ActivitySource ActivitySource = new("Warranty.Api");

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan? delay;
            try
            {
                delay = await ProcessNextAsync(stoppingToken) ? null : settings.PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Claim job polling failed; retrying in {Delay}.", settings.ErrorDelay);
                delay = settings.ErrorDelay;
            }

            if (delay is { } wait)
            {
                try
                {
                    await Task.Delay(wait, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Takes and runs the next job; returns false when the queue had none.</summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        ClaimJobLease? lease;
        await using (var pollScope = scopes.CreateAsyncScope())
        {
            lease = await pollScope.ServiceProvider.GetRequiredService<IJobQueue>()
                .TryDequeueAsync(_workerId, options.Value.LockDuration, ct);
        }

        if (lease is null)
        {
            return false;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = lease.JobId,
            ["ClaimId"] = lease.ClaimId,
            ["Round"] = lease.Round,
            ["TenantId"] = lease.TenantId,
            ["CorrelationId"] = lease.CorrelationId,
        });

        string? tenantSlug;
        await using (var tenantScope = scopes.CreateAsyncScope())
        {
            var tenant = await tenantScope.ServiceProvider.GetRequiredService<ITenantRepository>().GetActiveTenantAsync(lease.TenantId, ct);
            tenantSlug = tenant?.Slug;
        }

        if (tenantSlug is null)
        {
            // Without an active tenant there is no scope to run or settle the job in; its lease
            // expires and the queue stops offering it after the maximum attempts.
            logger.LogWarning("Claim job skipped: its tenant is not active.");
            return true;
        }

        using var tenantContext = TenantContextScope.Begin(
            lease.TenantId, tenantSlug, Principals.AdjudicationService, lease.CorrelationId);
        using var activity = StartActivity(lease);

        try
        {
            await using var runScope = scopes.CreateAsyncScope();
            await runScope.ServiceProvider.GetRequiredService<IAdjudicationRunner>().RunAsync(lease.ClaimId, lease.Round, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the lease expires and another worker resumes the run from its checkpoint.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Claim job failed with an infrastructure error.");
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            await using var failScope = scopes.CreateAsyncScope();
            await failScope.ServiceProvider.GetRequiredService<IJobQueue>().FailAsync(lease.JobId, $"{ex.GetType().Name}: {ex.Message}", ct);
            return true;
        }

        await using (var completeScope = scopes.CreateAsyncScope())
        {
            await completeScope.ServiceProvider.GetRequiredService<IJobQueue>().CompleteAsync(lease.JobId, ct);
        }

        return true;
    }

    /// <summary>A span for the job, joined to the submitting request's trace when the correlation ID is its trace ID.</summary>
    private static Activity? StartActivity(ClaimJobLease lease)
    {
        var parent = lease.CorrelationId.Length == 32 && lease.CorrelationId.All(char.IsAsciiHexDigitLower)
            ? new ActivityContext(ActivityTraceId.CreateFromString(lease.CorrelationId), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
            : default;
        return ActivitySource.StartActivity("claim-job", ActivityKind.Consumer, parent)
            ?.SetTag("warranty.job_id", lease.JobId)
            .SetTag("warranty.claim_id", lease.ClaimId)
            .SetTag("warranty.round", lease.Round)
            .SetTag("warranty.tenant_id", lease.TenantId);
    }
}
