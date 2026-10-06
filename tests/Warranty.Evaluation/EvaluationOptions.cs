namespace Warranty.Evaluation;

public enum EvaluationMode
{
    /// <summary>Model calls are answered from recorded fixtures: deterministic, free, no network.</summary>
    Replay,

    /// <summary>Real model calls through the configured routes: costs money, needs the Anthropic API key.</summary>
    Live,
}

/// <summary>Command line of the evaluation runner (quickstart §6).</summary>
public sealed record EvaluationOptions
{
    public const string Usage = """
        Usage: dotnet run --project tests/Warranty.Evaluation -- --mode replay|live [options]

          --mode replay|live       replay answers model calls from tests/fixtures/ai-recordings/golden/{caseId}/
                                   (deterministic, free); live calls the real models (costs money).
          --record                 with --mode live: write every model response as the case's replay fixture
                                   (replacing that case's earlier recording).
          --tenants a,b            only these tenants (default: all in the golden set).
          --cases G-AUR-01,...     only these case IDs.
          --output <dir>           report directory (default: artifacts/eval/{timestamp}).
          --review-db <conn>       PostgreSQL connection string of a production/staging database to read the
                                   human override rate from (default: the evaluation database).
          --embeddings <conn>      Ollama embedding connection string (Endpoint=...;Model=...) instead of the
                                   deterministic hash embeddings (retrieval then matches production).
        """;

    public EvaluationMode Mode { get; init; } = EvaluationMode.Replay;

    public bool Record { get; init; }

    public IReadOnlyList<string> Tenants { get; init; } = [];

    public IReadOnlyList<string> Cases { get; init; } = [];

    public string? OutputDirectory { get; init; }

    public string? ReviewDatabase { get; init; }

    public string? EmbeddingsConnection { get; init; }

    /// <summary>Parses the arguments; throws <see cref="ArgumentException"/> with a readable message on a usage error.</summary>
    public static EvaluationOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new EvaluationOptions();
        var modeGiven = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new ArgumentException($"{arg} needs a value.");

            switch (arg)
            {
                case "--mode":
                    var mode = Value();
                    options = options with
                    {
                        Mode = mode switch
                        {
                            "replay" => EvaluationMode.Replay,
                            "live" => EvaluationMode.Live,
                            _ => throw new ArgumentException($"Unknown mode '{mode}'; use replay or live."),
                        },
                    };
                    modeGiven = true;
                    break;
                case "--record":
                    options = options with { Record = true };
                    break;
                case "--tenants":
                    options = options with { Tenants = List(Value()) };
                    break;
                case "--cases":
                    options = options with { Cases = List(Value()) };
                    break;
                case "--output":
                    options = options with { OutputDirectory = Value() };
                    break;
                case "--review-db":
                    options = options with { ReviewDatabase = Value() };
                    break;
                case "--embeddings":
                    options = options with { EmbeddingsConnection = Value() };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        if (!modeGiven)
        {
            throw new ArgumentException("--mode replay|live is required.");
        }

        if (options.Record && options.Mode != EvaluationMode.Live)
        {
            throw new ArgumentException("--record needs --mode live: recordings are made from real model responses.");
        }

        return options;
    }

    private static List<string> List(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
