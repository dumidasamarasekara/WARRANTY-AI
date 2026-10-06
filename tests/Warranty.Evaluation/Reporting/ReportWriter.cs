using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.Evaluation.Metrics;

namespace Warranty.Evaluation.Reporting;

/// <summary>Writes <c>report.json</c> and <c>report.md</c> into the run's directory (quickstart §6).</summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteAsync(string directory, EvaluationReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, Json), ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), RenderMarkdown(report), ct);
    }

    public static string RenderMarkdown(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var m = report.Metrics;
        var md = new StringBuilder();
        md.AppendLine("# Golden-set evaluation report").AppendLine();
        md.AppendLine(Inv($"- Mode: **{report.Mode}**{(report.Record ? " (recording replay fixtures)" : string.Empty)}; embeddings: {report.EmbeddingProvider}"));
        md.AppendLine(Inv($"- Run: {report.StartedAt:u} to {report.FinishedAt:u}"));
        md.AppendLine(Inv($"- Tenants: {string.Join(", ", report.Tenants)}"));
        md.AppendLine(Inv($"- Cases: {report.CasesSelected} selected, **{report.CasesEvaluated} evaluated**, {report.CasesNotRecorded.Count} without recordings, {report.CasesFailed.Count} failed"));
        md.AppendLine();

        if (report.Notes.Count > 0)
        {
            md.AppendLine("## What this run measures").AppendLine();
            foreach (var note in report.Notes)
            {
                md.AppendLine($"- {note}");
            }

            md.AppendLine();
        }

        md.AppendLine("## Targets").AppendLine();
        md.AppendLine("| Metric | Target | Actual | Verdict |").AppendLine("|---|---|---|---|");
        foreach (var t in report.Targets)
        {
            md.AppendLine($"| {t.Metric} | {t.Target} | {t.Actual} | {Verdict(t.Passed)} |");
        }

        md.AppendLine(Inv($"| AI-unavailable fallback (cases without recordings) | none auto-finalized (FR-031) | {report.Fallback.AutoFinalized.Count} of {report.Fallback.Cases} auto-finalized | {Verdict(report.Fallback.Passed)} |"));
        md.AppendLine();

        md.AppendLine("## Recommendation and disposition accuracy").AppendLine();
        var tenants = m.Recommendation.ByTenant.Keys.Union(m.Disposition.ByTenant.Keys).Order(StringComparer.Ordinal).ToList();
        md.AppendLine("| | Overall |" + string.Concat(tenants.Select(t => $" {t} |")));
        md.AppendLine("|---|---|" + string.Concat(tenants.Select(_ => "---|")));
        md.AppendLine($"| Recommendation | {m.Recommendation.Overall} |" + string.Concat(tenants.Select(t => $" {Cell(m.Recommendation.ByTenant, t)} |")));
        md.AppendLine($"| Disposition | {m.Disposition.Overall} |" + string.Concat(tenants.Select(t => $" {Cell(m.Disposition.ByTenant, t)} |")));
        md.AppendLine();
        md.AppendLine("By expected label: " + string.Join("; ", m.Recommendation.ByExpected.Select(e => $"{e.Key} {e.Value}")) + ".");
        md.AppendLine();

        md.AppendLine("## Escalation recall").AppendLine();
        md.AppendLine($"- Should-escalate cases routed to human review: {m.Escalation.Recall}");
        md.AppendLine($"- Auto-finalized although labelled HumanReview: {List(m.Escalation.AutoFinalizedCaseIds)}");
        md.AppendLine($"- Labelled escalation reasons present: {m.Escalation.ReasonRecall}; missing: {List(m.Escalation.MissingReasons)}");
        md.AppendLine();

        md.AppendLine("## Confidence calibration").AppendLine();
        md.AppendLine(Inv($"- Brier score: {(m.Calibration.BrierScore is { } brier ? brier.ToString("0.000", CultureInfo.InvariantCulture) : "n/a")} over {m.Calibration.Count} valid recommendations (lower is better; a constant 50% scores 0.250)"));
        md.AppendLine().AppendLine("| Confidence band | Recommendations | Correct | Accuracy | Mean confidence |").AppendLine("|---|---|---|---|---|");
        foreach (var band in m.Calibration.Bands)
        {
            md.AppendLine(Inv($"| {band.Band} | {band.Count} | {band.Correct} | {Percent(band.Accuracy)} | {(band.MeanConfidence is { } mean ? mean.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")} |"));
        }

        md.AppendLine();
        md.AppendLine("## Retrieval recall@k of expected clause keys").AppendLine();
        md.AppendLine(Inv($"Scored cases: {m.Retrieval.CasesScored} (cases that expect a recommendation and list clause keys). ")
                      + string.Join(", ", m.Retrieval.Ks.Select(k => Inv($"recall@{k} = {Percent(m.Retrieval.MeanRecallAtK[k])}"))) + ".");
        md.AppendLine($"Cases whose run retrieved no clause: {List(m.Retrieval.CasesWithoutRetrieval)}");
        md.AppendLine();

        md.AppendLine("## Extraction field accuracy").AppendLine();
        md.AppendLine($"- Overall: {m.Extraction.Overall}");
        foreach (var (section, ratio) in m.Extraction.BySection)
        {
            md.AppendLine($"- {section}: {ratio}");
        }

        if (m.Extraction.Mismatches.Count > 0)
        {
            md.AppendLine().AppendLine("| Case | Section | Field | Expected | Actual |").AppendLine("|---|---|---|---|---|");
            foreach (var miss in m.Extraction.Mismatches)
            {
                md.AppendLine($"| {miss.CaseId} | {miss.Section} | {miss.Field} | `{Escape(miss.Expected)}` | `{Escape(miss.Actual ?? "missing")}` |");
            }
        }

        md.AppendLine();
        md.AppendLine("## Unsupported references").AppendLine();
        md.AppendLine(Inv($"- Recommendations checked: {m.UnsupportedReferences.Recommendations}; references cited: {m.UnsupportedReferences.CitedReferences}; not issued for the run: {m.UnsupportedReferences.Unsupported} ({Percent(m.UnsupportedReferences.Rate)})"));
        md.AppendLine($"- Details: {List(m.UnsupportedReferences.UnsupportedDetails)}");
        md.AppendLine();

        md.AppendLine("## Usage and cost (evaluated cases)").AppendLine();
        md.AppendLine("| Agent | Calls | Failed | Input | Output | Cache read | Cache write | Cost (USD) |").AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (agent, usage) in m.Usage.ByAgent)
        {
            md.AppendLine(UsageRow(agent, usage));
        }

        md.AppendLine(UsageRow("**total**", m.Usage.Total));
        md.AppendLine();
        md.AppendLine(Inv($"Cost per case: {(m.Usage.CostPerCase is { } perCase ? perCase.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a")} USD; cache-read share of input tokens: {Percent(m.Usage.CacheReadShare)}."));
        md.AppendLine();

        md.AppendLine("## Human override rate").AppendLine();
        if (report.HumanOverrideRate is { } overrides)
        {
            md.AppendLine($"Source: {overrides.Source}.").AppendLine();
            md.AppendLine("| Tenant | Review decisions | On a valid AI approve/reject | Overrides | Override rate |").AppendLine("|---|---|---|---|---|");
            foreach (var row in overrides.Tenants.Append(overrides.Total))
            {
                md.AppendLine(Inv($"| {row.Tenant} | {row.Decisions} | {row.OnAiDecisions} | {row.Overrides} | {Percent(row.Rate)} |"));
            }
        }
        else
        {
            md.AppendLine($"Not measured: {report.HumanOverrideRateError ?? "no database"}.");
        }

        md.AppendLine();
        md.AppendLine("## Cases").AppendLine();
        md.AppendLine("| Case | Tenant | Outcome | Expected rec. | Actual rec. | Conf. | Expected disp. | Actual disp. | Reasons | Note |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in report.Cases)
        {
            var actual = c.ActualRecommendation is null ? "none" : c.RecommendationValid ? c.ActualRecommendation : $"{c.ActualRecommendation} (invalid)";
            md.AppendLine(Inv($"| {c.CaseId} | {c.Tenant} | {c.Outcome} | {c.ExpectedRecommendation ?? "none"} | {actual} | {c.Confidence?.ToString(CultureInfo.InvariantCulture) ?? ""} | {c.ExpectedDisposition} | {c.ActualDisposition ?? "none"} | {string.Join(", ", c.ActualEscalationReasons)} | {Escape(c.Note ?? c.RunFailure ?? string.Empty)} |"));
        }

        return md.ToString();
    }

    private static string UsageRow(string name, UsageTotals u)
        => Inv($"| {name} | {u.ModelCalls} | {u.FailedCalls} | {u.InputTokens} | {u.OutputTokens} | {u.CacheReadTokens} | {u.CacheWriteTokens} | {u.Cost:0.0000} |");

    private static string Cell(IReadOnlyDictionary<string, Ratio> byTenant, string tenant)
        => byTenant.TryGetValue(tenant, out var ratio) ? ratio.ToString() : "n/a";

    private static string Verdict(bool? passed) => passed switch
    {
        true => "PASS",
        false => "**FAIL**",
        null => "not measured",
    };

    private static string Percent(double? value) => value is { } v ? v.ToString("P1", CultureInfo.InvariantCulture) : "n/a";

    private static string List(IReadOnlyCollection<string> items) => items.Count == 0 ? "none" : string.Join(", ", items);

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
