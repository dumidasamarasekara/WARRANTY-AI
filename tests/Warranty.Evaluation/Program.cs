namespace Warranty.Evaluation;

/// <summary>
/// The golden-dataset evaluation runner (research R19, quickstart §6), separate from production:
/// <c>--mode replay</c> scores recorded model behaviour deterministically and for free; <c>--mode live</c>
/// calls the real models (costs money) and with <c>--record</c> refreshes the replay fixtures. The report
/// goes to <c>artifacts/eval/{timestamp}/report.md</c> and <c>report.json</c>.
/// </summary>
/// <remarks>Exit codes: 0 the run completed (read the report for the verdicts), 1 the run failed, 2 usage error.</remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        EvaluationOptions options;
        try
        {
            options = EvaluationOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync(EvaluationOptions.Usage);
            return 2;
        }

        if (options.Mode == EvaluationMode.Live && !HasAnthropicKey())
        {
            await Console.Error.WriteLineAsync(
                "--mode live needs the Anthropic API key in ANTHROPIC_API_KEY (or AiGateway__Anthropic__ApiKey). Live runs cost money.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var (report, directory) = await new EvaluationRunner(options, RepositoryRoot()).RunAsync(cancellation.Token);
            Console.WriteLine();
            foreach (var target in report.Targets)
            {
                Console.WriteLine($"{target.Metric}: {target.Actual} (target {target.Target}) {(target.Passed switch { true => "PASS", false => "FAIL", null => "not measured" })}");
            }

            Console.WriteLine($"Cases: {report.CasesEvaluated} evaluated, {report.CasesNotRecorded.Count} without recordings, {report.CasesFailed.Count} failed.");
            Console.WriteLine($"Report: {Path.Combine(directory, "report.md")}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Evaluation failed: {ex}");
            return 1;
        }
    }

    private static bool HasAnthropicKey()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
           || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AiGateway__Anthropic__ApiKey"));

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Warranty.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException("Repository root (Warranty.slnx) not found.");
    }
}
