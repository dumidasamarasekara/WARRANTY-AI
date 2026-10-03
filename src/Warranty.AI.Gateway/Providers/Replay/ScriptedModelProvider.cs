using System.Text.Json;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Providers.Replay;

/// <summary>
/// An in-memory provider for unit tests: answers turns from a queue of scripted results and keeps
/// every request it received. Running out of scripted turns is a test bug and throws.
/// </summary>
internal sealed class ScriptedModelProvider(string name = ScriptedModelProvider.DefaultName) : IModelProvider
{
    public const string DefaultName = "scripted";

    private readonly Queue<Func<ResolvedTurnRequest, AiTurnResult>> _turns = new();
    private readonly List<ResolvedTurnRequest> _requests = [];
    private readonly Lock _lock = new();

    public string Name => name;

    public IReadOnlyList<ResolvedTurnRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToList();
            }
        }
    }

    public ScriptedModelProvider Enqueue(AiTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Enqueue(_ => result);
    }

    /// <summary>Scripts a turn computed from the request, e.g. to echo a tool call ID.</summary>
    public ScriptedModelProvider Enqueue(Func<ResolvedTurnRequest, AiTurnResult> turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        lock (_lock)
        {
            _turns.Enqueue(turn);
        }

        return this;
    }

    public Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<ResolvedTurnRequest, AiTurnResult> turn;
        lock (_lock)
        {
            _requests.Add(request);
            if (!_turns.TryDequeue(out turn!))
            {
                throw new InvalidOperationException($"No scripted turn is left for call {_requests.Count} ({request.Request.Context.Agent}).");
            }
        }

        return Task.FromResult(turn(request));
    }

    /// <summary>A finished turn whose structured output is <paramref name="json"/>.</summary>
    public static AiTurnResult Completed(string json, string model = "scripted-model")
        => new ReplayRecording { Stop = AiStopKind.Completed, StructuredOutput = Parse(json) }.ToResult(DefaultName, model);

    /// <summary>A turn that asks for one or more tool calls.</summary>
    public static AiTurnResult ToolCalls(params AiToolCall[] calls)
        => new ReplayRecording
        {
            Stop = AiStopKind.ToolCalls,
            ToolCalls = calls.Select(c => new ReplayToolCall(c.CallId, c.ToolName, c.Arguments)).ToList(),
        }.ToResult(DefaultName, "scripted-model");

    public static AiTurnResult Failed(AiFailureKind kind, string message = "Scripted failure.")
        => new ReplayRecording { Stop = AiStopKind.Failed, Failure = new ReplayFailure(kind, message) }.ToResult(DefaultName, "scripted-model");

    public static AiTurnResult Stopped(AiStopKind stop)
        => new ReplayRecording { Stop = stop }.ToResult(DefaultName, "scripted-model");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
