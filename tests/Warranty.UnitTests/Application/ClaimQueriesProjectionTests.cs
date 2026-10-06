using Warranty.Application.Abstractions;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;

namespace Warranty.UnitTests.Application;

/// <summary>The claims-agent projection rules of the claim detail (FR-005).</summary>
public sealed class ClaimQueriesProjectionTests
{
    [Theory]
    [InlineData(new[] { Principals.ClaimsAgentRole }, true)]
    [InlineData(new[] { Principals.ClaimsReviewerRole }, false)]
    [InlineData(new[] { Principals.AuditorRole }, false)]
    [InlineData(new[] { Principals.ClaimsAgentRole, Principals.ClaimsReviewerRole }, false)]
    public void Only_agents_without_a_reviewer_or_auditor_role_get_the_restricted_view(string[] roles, bool restricted)
        => ClaimQueries.IsRestrictedToAgentView(roles.ToHashSet()).ShouldBe(restricted);

    [Fact]
    public void Risk_related_reasons_collapse_into_one_label_for_the_restricted_view()
    {
        EscalationReason[] reasons =
        [
            EscalationReason.ValueAboveLimit, EscalationReason.RiskMedium, EscalationReason.EvidenceConflict,
            EscalationReason.AiDeterministicDisagreement, EscalationReason.UnsafeClaimantText, EscalationReason.AiUnavailable,
        ];

        ClaimQueries.ReasonsFor(reasons, restricted: true)
            .ShouldBe(["VALUE_ABOVE_LIMIT", EscalationReasonExtensions.AgentSafeLabel, "AI_UNAVAILABLE"]);
        ClaimQueries.ReasonsFor(reasons, restricted: false)
            .ShouldBe(
            [
                "VALUE_ABOVE_LIMIT", "RISK_MEDIUM", "EVIDENCE_CONFLICT", "AI_DETERMINISTIC_DISAGREEMENT", "UNSAFE_CLAIMANT_TEXT",
                "AI_UNAVAILABLE",
            ]);
    }

    [Fact]
    public void The_etag_is_the_quoted_row_version()
        => ClaimQueries.FormatETag(4711).ShouldBe("\"4711\"");
}
