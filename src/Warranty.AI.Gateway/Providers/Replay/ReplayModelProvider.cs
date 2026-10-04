using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Providers.Replay;

/// <summary>
/// Numbers the model calls of each agent per claim (falling back to the run), so the n-th call of an
/// agent reads <c>{agent}-{n}.json</c>. Numbering continues across rounds of the same claim, so a
/// scenario with a supplement round simply has higher call indexes. Registered as a singleton.
/// </summary>
public sealed class ReplayCallCounter
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public int Next(AiCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var owner = context.ClaimId?.ToString() ?? context.RunId?.ToString() ?? "none";
        return _counts.AddOrUpdate($"{owner}/{context.Agent}", 1, (_, count) => count + 1);
    }
}

/// <summary>
/// The <c>replay</c> provider (contracts/ai-gateway.md, research R18): answers every chat route from
/// recorded responses so scenarios run deterministically without network access or cost. With
/// <see cref="ReplayProviderOptions.Record"/> it instead calls the live provider and writes each
/// response as the fixture a later replay will read. A missing fixture is a failed turn, which the
/// harness escalates like any other AI failure.
/// </summary>
internal sealed partial class ReplayModelProvider(
    IReplayScenarioSelector scenarios,
    ReplayCallCounter counter,
    IOptions<AiGatewayOptions> options,
    ILogger<ReplayModelProvider> logger,
    IModelProvider? recordFrom = null) : IModelProvider
{
    public const string ProviderName = AiGatewayOptions.ReplayMode;

    public string Name => ProviderName;

    public async Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Request.Context;
        var model = request.Route.Options.Model;
        if (!AgentName().IsMatch(context.Agent))
        {
            return Failure(model, $"Agent name '{context.Agent}' cannot name a replay fixture.");
        }

        var scenario = await scenarios.SelectScenarioAsync(context, ct);
        if (scenario is null || !AgentName().IsMatch(scenario))
        {
            return Failure(model, $"No replay scenario matches claim {context.ClaimId?.ToString() ?? "(none)"}.");
        }

        var path = Path.Combine(
            ReplayPaths.Resolve(options.Value.Replay.RecordingsPath), scenario, $"{context.Agent}-{counter.Next(context)}.json");
        var variables = await scenarios.GetFixtureVariablesAsync(context, ct);

        return options.Value.Replay.Record
            ? await RecordAsync(request, path, variables, ct)
            : await ReplayAsync(path, model, variables, ct);
    }

    private async Task<AiTurnResult> ReplayAsync(string path, string model, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning("Replay fixture {Path} does not exist.", path);
            return Failure(model, $"Replay fixture '{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}' does not exist.");
        }

        // Placeholders such as {{claim.purchaseDate}} take the claim's values (ReplayFixtureVariables).
        var json = ReplayFixtureVariables.Apply(await File.ReadAllTextAsync(path, ct), variables);
        var recording = JsonSerializer.Deserialize<ReplayRecording>(json, ReplayRecording.JsonOptions)
                        ?? throw new InvalidDataException($"Replay fixture '{path}' is empty.");
        return recording.ToResult(ProviderName, model);
    }

    private async Task<AiTurnResult> RecordAsync(
        ResolvedTurnRequest request, string path, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        var live = recordFrom ?? throw new InvalidOperationException("Replay recording needs a live provider.");
        var result = await live.CompleteAsync(request, ct);

        // The claim's dates are written back as placeholders, so the fixture replays on any day.
        var json = ReplayFixtureVariables.Templatize(
            JsonSerializer.Serialize(ReplayRecording.FromResult(result), ReplayRecording.JsonOptions), variables);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, ct);

        logger.LogInformation("Recorded {Route} turn to {Path}.", request.Route.Name, path);
        return result;
    }

    private static AiTurnResult Failure(string model, string message)
        => new(
            AiStopKind.Failed, new AiMessage(AiRole.Assistant, []), [], null, AiUsage.None(ProviderName, model),
            new AiFailure(AiFailureKind.ProviderError, message));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex AgentName();
}
