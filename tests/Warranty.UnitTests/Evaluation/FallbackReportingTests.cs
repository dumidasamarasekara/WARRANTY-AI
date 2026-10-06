using Warranty.Application.Abstractions.Storage;
using Warranty.Application.Claims;
using Warranty.Evaluation.Golden;
using Warranty.Evaluation.Metrics;
using Warranty.Evaluation.Reporting;
using Warranty.Infrastructure.Storage;
using static Warranty.UnitTests.Evaluation.EvaluationCases;

namespace Warranty.UnitTests.Evaluation;

/// <summary>
/// Replay cases without recordings (every model call fails): the AI-unavailable fallback check, the
/// classification of reasons that only say a step did not run, and the submission of golden cases that lack
/// an invoice or a photo.
/// </summary>
public sealed class FallbackReportingTests
{
    [Fact]
    public void No_applicable_policy_comes_from_a_skipped_step_only_when_the_policy_step_did_not_run()
    {
        var skipped = Observe(reasons: ["AI_UNAVAILABLE", "NO_APPLICABLE_POLICY"]) with { PolicyStepRan = false };
        var looked = skipped with { PolicyStepRan = true };

        skipped.ReasonsFromSkippedSteps.ShouldBe(["NO_APPLICABLE_POLICY"]);
        looked.ReasonsFromSkippedSteps.ShouldBeEmpty();
        (Observe(reasons: ["AI_UNAVAILABLE"]) with { PolicyStepRan = false }).ReasonsFromSkippedSteps.ShouldBeEmpty();
    }

    [Fact]
    public void The_fallback_fails_only_on_auto_finalized_cases_and_lists_the_rest()
    {
        var cases = new[]
        {
            Observe("G-AUR-01", disposition: "HumanReview", reasons: ["AI_UNAVAILABLE", "NO_APPLICABLE_POLICY"], outcome: CaseOutcomeKind.NotRecorded),
            Observe("G-AUR-16", disposition: "RequestInformation", outcome: CaseOutcomeKind.NotRecorded),
        };

        var check = FallbackCheck.Compute(cases);

        check.Passed.ShouldBe(true);
        check.AutoFinalized.ShouldBeEmpty();
        check.WithoutAiUnavailable.ShouldBe(["G-AUR-16"]);
        check.PolicyStepNotRun.ShouldBe(["G-AUR-01", "G-AUR-16"]);
        check.Dispositions.ShouldBe(new Dictionary<string, int> { ["HumanReview"] = 1, ["RequestInformation"] = 1 });

        var finalized = FallbackCheck.Compute([.. cases, Observe("G-AUR-02", disposition: "AutoApprove", outcome: CaseOutcomeKind.NotRecorded)]);
        finalized.Passed.ShouldBe(false);
        finalized.AutoFinalized.ShouldBe(["G-AUR-02"]);
    }

    [Fact]
    public void The_report_marks_a_no_applicable_policy_reason_whose_policy_step_did_not_run()
    {
        var observation = Observe(
            "G-AUR-01", recommendation: null, valid: false, confidence: null, disposition: "HumanReview",
            reasons: ["AI_UNAVAILABLE", "NO_APPLICABLE_POLICY"], outcome: CaseOutcomeKind.NotRecorded);
        var scored = new[] { Scored(Expect(), observation) };
        var metrics = EvaluationMetrics.Compute(scored);
        var report = new EvaluationReport
        {
            StartedAt = DateTimeOffset.UnixEpoch,
            FinishedAt = DateTimeOffset.UnixEpoch,
            Mode = "replay",
            Record = false,
            EmbeddingProvider = "hash",
            Tenants = ["aurora"],
            CasesSelected = 1,
            CasesEvaluated = 0,
            CasesNotRecorded = ["G-AUR-01"],
            CasesFailed = [],
            Metrics = metrics,
            Targets = metrics.Targets(),
            Fallback = FallbackCheck.Compute([observation]),
            Notes = [],
            Cases =
            [
                new CaseSummary(
                    "G-AUR-01", "aurora", "NotRecorded", null, "APPROVE", null, false, null, "AutoApprove", "HumanReview", "UnderReview",
                    [], observation.EscalationReasons, "intake: Failed", observation.ReasonsFromSkippedSteps),
            ],
            Observations = [observation],
        };

        var markdown = ReportWriter.RenderMarkdown(report);

        markdown.ShouldContain("| AI_UNAVAILABLE, NO_APPLICABLE_POLICY (policy step not run) |");
        markdown.ShouldContain("- Policy step not run: 1 of 1.");
    }

    [Fact]
    public void Only_the_intake_short_circuit_cases_lack_an_invoice_or_a_photo()
    {
        var cases = GoldenCase.Load(Path.Combine(RepositoryRoot(), "seed", "golden", "golden-claims.json"));

        cases.Where(c => c.NeedsInvoicePlaceholder).Select(c => c.CaseId).ShouldBe(["G-AUR-16"]);
        cases.Where(c => c.NeedsPhotoPlaceholder).Select(c => c.CaseId).ShouldBe(["G-BOR-18"]);
    }

    [Fact]
    public async Task The_placeholder_passes_submission_checks_as_an_invoice_and_as_a_photo()
    {
        EvidenceFileSignature.Detect(PlaceholderEvidence.Png.Span).ShouldBe(EvidenceFileType.Png);

        await using var content = new MemoryStream(PlaceholderEvidence.Png.ToArray(), writable: false);
        var result = await new UploadSanitizer().SanitizeAsync(content, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<UploadSanitizerResult.Sanitized>().Type.ShouldBe(EvidenceFileType.Png);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Warranty.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Warranty.slnx) not found.");
    }
}
