using System.Text.Json;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;

namespace Warranty.AI.Gateway.Providers.Replay;

/// <summary>Chooses which recorded scenario answers a model call.</summary>
public interface IReplayScenarioSelector
{
    /// <summary>The scenario ID (e.g. <c>S1</c>), or null when the call belongs to no known scenario.</summary>
    Task<string?> SelectScenarioAsync(AiCallContext context, CancellationToken ct);
}

/// <summary>One entry of <c>seed/golden/scenarios.json</c>, reduced to what replay needs.</summary>
public sealed record ReplayScenario(string ScenarioId, string? Tenant, string Serial);

/// <summary>
/// The golden scenario list. A scenario is found by the claim's serial number, optionally narrowed to
/// the tenant slug so the same serial can belong to different scenarios in different tenants.
/// </summary>
public sealed class ReplayScenarioCatalog
{
    private readonly IReadOnlyList<ReplayScenario> _scenarios;

    public ReplayScenarioCatalog(IEnumerable<ReplayScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        _scenarios = scenarios
            .Select(s => s with { Serial = ProductSerial.NormalizeSerial(s.Serial) })
            .ToList();
    }

    public IReadOnlyList<ReplayScenario> Scenarios => _scenarios;

    /// <summary>Reads the scenario list; a missing file means no scenarios (every replayed call then fails).</summary>
    public static ReplayScenarioCatalog Load(IOptions<AiGatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = ReplayPaths.Resolve(options.Value.Replay.ScenariosPath);
        if (!File.Exists(path))
        {
            return new ReplayScenarioCatalog([]);
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var entries = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.GetProperty("scenarios");
        return new ReplayScenarioCatalog(entries.EnumerateArray().Select(Parse).ToList());
    }

    /// <summary>The first scenario for the serial in this tenant (or with no tenant).</summary>
    public string? Find(string serialNumber, string? tenantSlug)
    {
        var serial = ProductSerial.NormalizeSerial(serialNumber);
        return _scenarios.FirstOrDefault(s =>
                s.Serial == serial
                && (s.Tenant is null || string.Equals(s.Tenant, tenantSlug, StringComparison.OrdinalIgnoreCase)))
            ?.ScenarioId;
    }

    private static ReplayScenario Parse(JsonElement entry)
    {
        static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return new ReplayScenario(
            Text(entry, "scenarioId") ?? throw new FormatException("A scenario has no 'scenarioId'."),
            Text(entry, "tenant"),
            Text(entry, "serial") ?? throw new FormatException($"Scenario '{Text(entry, "scenarioId")}' has no 'serial'."));
    }
}

/// <summary>Maps a call to its scenario through the serial number of the claim it belongs to.</summary>
internal sealed class SerialNumberScenarioSelector(
    IClaimRepository claims,
    ITenantContext tenantContext,
    ReplayScenarioCatalog catalog) : IReplayScenarioSelector
{
    public async Task<string?> SelectScenarioAsync(AiCallContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ClaimId is not { } claimId)
        {
            return null;
        }

        var claim = await claims.GetAsync(claimId, ct);
        return claim is null ? null : catalog.Find(claim.SerialNumber, tenantContext.TenantSlug);
    }
}

/// <summary>Resolves repository-relative replay paths from wherever the process runs (tests, API, tools).</summary>
internal static class ReplayPaths
{
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, path);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return Path.GetFullPath(path);
    }
}
