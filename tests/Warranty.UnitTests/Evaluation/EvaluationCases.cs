using System.Text.Json.Nodes;
using Warranty.Evaluation.Golden;
using Warranty.Evaluation.Metrics;

namespace Warranty.UnitTests.Evaluation;

/// <summary>Builders for golden labels and observations in the evaluation metric tests.</summary>
internal static class EvaluationCases
{
    public static readonly DateOnly ClaimDate = new(2026, 10, 6);

    public static GoldenExpectation Expect(
        string? recommendation = "APPROVE",
        string disposition = "AutoApprove",
        string[]? reasons = null,
        string[]? clauses = null,
        JsonObject? extraction = null)
        => new(recommendation, disposition, null, null, null, [], reasons ?? [], [], clauses ?? [], extraction);

    public static CaseObservation Observe(
        string caseId = "G-AUR-01",
        string tenant = "aurora",
        string? recommendation = "APPROVE",
        bool valid = true,
        int? confidence = 90,
        string? disposition = "AutoApprove",
        string[]? reasons = null,
        RetrievedClause[]? retrieved = null,
        string[]? issued = null,
        string[]? cited = null,
        CaseOutcomeKind outcome = CaseOutcomeKind.Evaluated)
        => new()
        {
            CaseId = caseId,
            Tenant = tenant,
            Outcome = outcome,
            ClaimDate = ClaimDate,
            Recommendation = recommendation,
            RecommendationValid = valid,
            Confidence = confidence,
            Disposition = disposition,
            EscalationReasons = reasons ?? [],
            RetrievedClauses = retrieved ?? [],
            IssuedReferences = issued ?? [],
            CitedReferences = cited ?? [],
        };

    public static ScoredCase Scored(GoldenExpectation expected, CaseObservation observation)
        => new(observation.CaseId, observation.Tenant, expected, observation);
}
