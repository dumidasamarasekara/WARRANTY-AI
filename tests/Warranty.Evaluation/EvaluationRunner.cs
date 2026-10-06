using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Warranty.Evaluation.Golden;
using Warranty.Evaluation.Metrics;
using Warranty.Evaluation.Reporting;

namespace Warranty.Evaluation;

/// <summary>
/// Runs the selected golden cases one after the other in a fresh evaluation environment, scores them and
/// writes the report. In replay mode the recordings of a case live in
/// <c>tests/fixtures/ai-recordings/golden/{caseId}/{agent}-{n}.json</c>, selected by the case's tenant and
/// serial (the runner writes that scenario list for the replay provider); <c>--mode live --record</c> writes
/// them. A replay case without recordings is still run — every model call then fails — and is scored only
/// on the AI-unavailable fallback, never on model quality.
/// </summary>
internal sealed class EvaluationRunner(EvaluationOptions options, string repositoryRoot)
{
    private const string HashEmbeddings = "hash embeddings (deterministic, not the production model)";

    private string SeedPath => Path.Combine(repositoryRoot, "seed");

    private string RecordingsRoot => Path.Combine(repositoryRoot, "tests", "fixtures", "ai-recordings", "golden");

    public async Task<(EvaluationReport Report, string Directory)> RunAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var output = options.OutputDirectory is { } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(repositoryRoot, "artifacts", "eval", startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);

        var all = GoldenCase.Load(Path.Combine(SeedPath, "golden", "golden-claims.json"));
        var selected = Select(all);
        var tenants = LoadTenants();
        var recorded = selected.Where(c => options.Mode == EvaluationMode.Live || HasRecordings(c.CaseId)).Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal);

        var scenariosFile = Path.Combine(output, "replay-scenarios.json");
        await WriteScenarioListAsync(all, scenariosFile, ct);

        await using var environment = new EvaluationEnvironment();
        Console.WriteLine($"Starting the evaluation environment (containers, migrations, seed) for {selected.Count} cases...");
        await environment.StartAsync(SeedPath, AiSettings(scenariosFile), ct);

        var (overrides, overridesError) = await QueryOverrideRateAsync(environment, ct);
        var runner = new CaseRunner(environment.Services, environment.OwnerConnectionString(), Path.Combine(SeedPath, "evidence"), TimeProvider.System);
        var scored = new List<ScoredCase>();
        foreach (var golden in selected)
        {
            var kind = recorded.Contains(golden.CaseId) ? CaseOutcomeKind.Evaluated : CaseOutcomeKind.NotRecorded;
            if (options.Record)
            {
                // A fresh recording replaces the old one, so stale higher-numbered calls cannot linger.
                var directory = Path.Combine(RecordingsRoot, golden.CaseId);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            CaseObservation observation;
            try
            {
                observation = await runner.RunAsync(golden, tenants[golden.Tenant], kind, ct);
                if (kind == CaseOutcomeKind.NotRecorded)
                {
                    observation = observation with { Note = "no recordings: every model call failed (fallback check only)" };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observation = new CaseObservation
                {
                    CaseId = golden.CaseId,
                    Tenant = golden.Tenant,
                    Outcome = CaseOutcomeKind.Failed,
                    Note = $"{ex.GetType().Name}: {ex.Message}",
                };
            }

            Console.WriteLine($"  {golden.CaseId,-9} {observation.Outcome,-11} expected {golden.Expected.Recommendation ?? "none",-24} / {golden.Expected.Disposition,-18} "
                              + $"got {observation.Recommendation ?? "none",-24} / {observation.Disposition ?? "none"}");
            scored.Add(new ScoredCase(golden.CaseId, golden.Tenant, golden.Expected, observation));
        }

        var metrics = EvaluationMetrics.Compute(scored);
        var report = new EvaluationReport
        {
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            Mode = options.Mode == EvaluationMode.Live ? "live" : "replay",
            Record = options.Record,
            EmbeddingProvider = options.EmbeddingsConnection is null ? HashEmbeddings : "ollama (production embedding model)",
            Tenants = selected.Select(c => c.Tenant).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            CasesSelected = selected.Count,
            CasesEvaluated = metrics.CasesEvaluated,
            CasesNotRecorded = Ids(scored, CaseOutcomeKind.NotRecorded),
            CasesFailed = Ids(scored, CaseOutcomeKind.Failed),
            Metrics = metrics,
            Targets = metrics.Targets(),
            Fallback = FallbackCheck.Compute(scored.Select(s => s.Observation).Where(o => o.Outcome == CaseOutcomeKind.NotRecorded)),
            HumanOverrideRate = overrides,
            HumanOverrideRateError = overridesError,
            Notes = Notes(selected.Count, recorded.Count),
            Cases = scored.Select(Summarize).ToList(),
            Observations = scored.Select(s => s.Observation).ToList(),
        };

        await ReportWriter.WriteAsync(output, report, ct);
        return (report, output);
    }

    private List<GoldenCase> Select(IReadOnlyList<GoldenCase> all)
    {
        var selected = all
            .Where(c => options.Tenants.Count == 0 || options.Tenants.Contains(c.Tenant, StringComparer.OrdinalIgnoreCase))
            .Where(c => options.Cases.Count == 0 || options.Cases.Contains(c.CaseId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var unknown = options.Cases.Where(id => !all.Any(c => string.Equals(c.CaseId, id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown golden case(s): {string.Join(", ", unknown)}.");
        }

        return selected.Count > 0 ? selected : throw new ArgumentException("No golden case matches --tenants/--cases.");
    }

    private bool HasRecordings(string caseId)
    {
        var directory = Path.Combine(RecordingsRoot, caseId);
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any();
    }

    private Dictionary<string, EvaluationTenant> LoadTenants()
        => Directory.EnumerateDirectories(Path.Combine(SeedPath, "tenants"))
            .Select(folder => JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "tenant.json")))!)
            .Select(t => new EvaluationTenant(Guid.Parse((string)t["id"]!), (string)t["slug"]!))
            .ToDictionary(t => t.Slug, StringComparer.Ordinal);

    /// <summary>The replay provider's scenario list for the golden cases: scenario = case ID, picked by tenant and serial.</summary>
    private static async Task WriteScenarioListAsync(IReadOnlyList<GoldenCase> cases, string path, CancellationToken ct)
    {
        var scenarios = new JsonArray(cases
            .Select(c => (JsonNode)new JsonObject { ["scenarioId"] = c.CaseId, ["tenant"] = c.Tenant, ["serial"] = c.Serial })
            .ToArray());
        await File.WriteAllTextAsync(path, new JsonObject { ["scenarios"] = scenarios }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private Dictionary<string, string?> AiSettings(string scenariosFile)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (options.EmbeddingsConnection is { } embeddings)
        {
            settings["ConnectionStrings:embeddings"] = embeddings;
            settings["AiGateway:Routes:embedding:Provider"] = "ollama";
            settings["AiGateway:Routes:embedding:Model"] = "nomic-embed-text";
        }
        else
        {
            settings["AiGateway:Routes:embedding:Provider"] = "hash";
            settings["AiGateway:Routes:embedding:Model"] = "hash-embedding";
        }

        settings["AiGateway:Routes:embedding:Dimensions"] = "768";
        if (options.Mode == EvaluationMode.Live && !options.Record)
        {
            settings["AiGateway:Mode"] = "live";
            return settings;
        }

        // Replay reads the case's recordings; recording is replay mode that asks the live provider and writes them.
        settings["AiGateway:Mode"] = "replay";
        settings["AiGateway:Replay:Record"] = options.Record ? "true" : "false";
        settings["AiGateway:Replay:RecordingsPath"] = RecordingsRoot;
        settings["AiGateway:Replay:ScenariosPath"] = scenariosFile;
        return settings;
    }

    private async Task<(HumanOverrideRateResult? Result, string? Error)> QueryOverrideRateAsync(EvaluationEnvironment environment, CancellationToken ct)
    {
        try
        {
            return options.ReviewDatabase is { } reviewDb
                ? (await HumanOverrideRate.QueryAsync(reviewDb, "review database given with --review-db", ct), null)
                : (await HumanOverrideRate.QueryAsync(
                    environment.OwnerConnectionString(), "the evaluation database (seeded; it holds no reviewer decisions — pass --review-db for production)", ct), null);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or ArgumentException)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private List<string> Notes(int selected, int recorded)
    {
        var notes = new List<string>();
        if (options.Mode == EvaluationMode.Replay)
        {
            notes.Add($"Replay: model calls were answered from tests/fixtures/ai-recordings/golden/; {recorded} of {selected} selected cases have recordings and only those are scored. "
                      + "Model-dependent figures (extraction, recommendation, confidence, citations) reproduce the recorded outputs and change only when the recordings are refreshed "
                      + "with --mode live --record; the deterministic parts (validation, risk rules, guardrails, retrieval ranking) are re-measured on every run.");
            if (recorded < selected)
            {
                notes.Add($"{selected - recorded} cases have no recordings: they ran with every model call failing, are excluded from every quality metric "
                          + "and only checked for the AI-unavailable fallback (none may be finalized automatically, FR-031). No model output was invented for them.");
            }
        }
        else
        {
            notes.Add("Live: real model calls through the configured routes; results vary between runs and the run cost is shown under Usage."
                      + (options.Record ? " Every response was recorded as the case's replay fixture." : string.Empty));
            notes.Add("Invoices dated by an offset print the date of their generation day; regenerate them as of the claim date before a live run "
                      + "(dotnet run --project tools/Warranty.EvidenceGenerator -- generate --spec seed/evidence/golden/evidence.json --as-of <claim date>).");
        }

        if (options.EmbeddingsConnection is null)
        {
            notes.Add("Retrieval uses deterministic hash embeddings, not the production embedding model; recall@k describes that index. Pass --embeddings with an Ollama connection to match production.");
        }

        notes.Add("Cases evaluated in a later round (setup.round > 1) upload all their evidence with the submission and reach the round through the claim's own transitions, "
                  + "so the supplement is stored with round 1; the harness reads evidence of every round up to the evaluated one.");
        notes.Add("Human override rate comes from review decisions, not from the golden cases (research R19).");
        return notes;
    }

    private static List<string> Ids(IEnumerable<ScoredCase> scored, CaseOutcomeKind kind)
        => scored.Where(s => s.Observation.Outcome == kind).Select(s => s.CaseId).ToList();

    private static CaseSummary Summarize(ScoredCase s)
        => new(
            s.CaseId,
            s.Tenant,
            s.Observation.Outcome.ToString(),
            s.Observation.Note,
            s.Expected.Recommendation,
            s.Observation.Recommendation,
            s.Observation.RecommendationValid,
            s.Observation.Confidence,
            s.Expected.Disposition,
            s.Observation.Disposition,
            s.Observation.Status,
            s.Expected.EscalationReasons,
            s.Observation.EscalationReasons,
            s.Observation.RunFailure);
}
