using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Warranty.Api.Workers;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Adjudication;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Tenancy;

namespace Warranty.UnitTests.Api;

public sealed class ClaimJobWorkerTests
{
    private static readonly Tenant Aurora = Tenant.Create(
        Guid.Parse("11111111-1111-7111-8111-111111111111"), "aurora", "Aurora Electronics", DateTimeOffset.UnixEpoch);

    private static readonly ClaimJobLease Lease = new(Guid.NewGuid(), Aurora.Id, Guid.NewGuid(), 2, "0af7651916cd43dd8448eb211c80319c");

    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IAdjudicationRunner _runner = Substitute.For<IAdjudicationRunner>();
    private readonly FakeTimeProvider _time = new();

    public ClaimJobWorkerTests()
    {
        _tenants.GetActiveTenantAsync(Aurora.Id, Arg.Any<CancellationToken>()).Returns(Aurora);
    }

    [Fact]
    public async Task An_empty_queue_runs_nothing()
    {
        var worker = Worker();

        (await worker.ProcessNextAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_job_runs_in_its_tenant_scope_as_the_adjudication_service_and_is_completed()
    {
        _queue.TryDequeueAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Lease);
        ITenantContext? seen = null;
        _runner.RunAsync(Lease.ClaimId, Lease.Round, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var current = TenantContextScope.Current!;
                seen = new Snapshot(current.TenantId, current.TenantSlug, current.PrincipalId, current.IsSystem, current.CorrelationId);
                return Task.CompletedTask;
            });

        (await Worker().ProcessNextAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        seen.ShouldNotBeNull();
        seen.TenantId.ShouldBe(Aurora.Id);
        seen.TenantSlug.ShouldBe("aurora");
        seen.PrincipalId.ShouldBe(Principals.AdjudicationService);
        seen.IsSystem.ShouldBeTrue();
        seen.CorrelationId.ShouldBe(Lease.CorrelationId);
        await _queue.Received(1).CompleteAsync(Lease.JobId, Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, TestContext.Current.CancellationToken);
        TenantContextScope.Current.ShouldBeNull();
    }

    [Fact]
    public async Task An_infrastructure_exception_fails_the_job_for_a_retry()
    {
        _queue.TryDequeueAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Lease);
        _runner.RunAsync(Lease.ClaimId, Lease.Round, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("database timeout"));

        (await Worker().ProcessNextAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        await _queue.Received(1).FailAsync(Lease.JobId, Arg.Is<string>(r => r.Contains("database timeout")), Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_during_a_run_leaves_the_job_leased_for_another_worker()
    {
        using var cts = new CancellationTokenSource();
        _queue.TryDequeueAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Lease);
        _runner.RunAsync(Lease.ClaimId, Lease.Round, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Worker().ProcessNextAsync(cts.Token));

        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, TestContext.Current.CancellationToken);
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_job_of_an_inactive_tenant_is_not_run()
    {
        _queue.TryDequeueAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Lease with { TenantId = Guid.NewGuid() });

        (await Worker().ProcessNextAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default, default, TestContext.Current.CancellationToken);
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_background_loop_processes_jobs_until_stopped()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.TryDequeueAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Lease, (ClaimJobLease?)null);
        _runner.RunAsync(Lease.ClaimId, Lease.Round, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ran.SetResult();
            return Task.CompletedTask;
        });
        var worker = Worker();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        await _queue.Received(1).CompleteAsync(Lease.JobId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void The_worker_is_registered_unless_disabled(string? enabled, bool registered)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClaimJobWorker:Enabled"] = enabled })
            .Build();

        var services = new ServiceCollection().AddClaimJobWorker(configuration);

        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ClaimJobWorker)).ShouldBe(registered);
    }

    private ClaimJobWorker Worker()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _queue);
        services.AddScoped(_ => _tenants);
        services.AddScoped(_ => _runner);
        var provider = services.BuildServiceProvider();
        return new ClaimJobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ClaimJobWorkerOptions()),
            _time,
            NullLogger<ClaimJobWorker>.Instance);
    }

    private sealed record Snapshot(Guid TenantId, string TenantSlug, string PrincipalId, bool IsSystem, string CorrelationId) : ITenantContext
    {
        public bool IsResolved => true;

        public string KnowledgeNamespace => $"tenant-{TenantSlug}";

        public string PrincipalName => PrincipalId;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
    }
}
