using System.Diagnostics;

namespace Warranty.AI.Harness.Execution;

/// <summary>Spans for harness steps, agents, model turns and tool calls (constitution VII).</summary>
public interface ITraceWriter
{
    /// <summary>Starts a span; dispose it to end it. Returns null when nobody listens.</summary>
    IDisposable? StartSpan(string name, IReadOnlyDictionary<string, object?>? tags = null);
}

/// <summary>Writes spans to <see cref="ActivitySource"/> <c>Warranty.AI.Harness</c>, exported by the service defaults.</summary>
public sealed class ActivityTraceWriter : ITraceWriter
{
    public const string SourceName = "Warranty.AI.Harness";

    private static readonly ActivitySource Source = new(SourceName);

    public IDisposable? StartSpan(string name, IReadOnlyDictionary<string, object?>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var activity = Source.StartActivity(name);
        if (activity is not null && tags is not null)
        {
            foreach (var (key, value) in tags)
            {
                activity.SetTag(key, value);
            }
        }

        return activity;
    }
}
