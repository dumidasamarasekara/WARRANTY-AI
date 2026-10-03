using System.Reflection;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Audit;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Review;

namespace Warranty.UnitTests.Domain;

public sealed class AdjudicationReviewAuditTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private const string ClaimantText = "Your laptop is covered and will be repaired.";
    private const string Reason = "Invoice total differs from the model value.";

    private static Recommendation Valid(AiDecision decision) => Recommendation.CreateValid(
        Guid.CreateVersion7(), Tenant, "{}", decision, CoverageDetermination.Covered, 92, "summary", "explanation",
        [], [], [], false, "claude-opus-5-5", "decision", "v1");

    private static Recommendation Invalid() => Recommendation.CreateInvalid(
        Guid.CreateVersion7(), Tenant, "{}", ["schema"], "claude-opus-5-5", "decision", "v1", AiDecision.Approve);

    private static ReviewDecision Decide(
        ReviewDecisionKind kind, Recommendation? ai, string? justification = null, string? claimantExplanation = null,
        IEnumerable<RequestedItem>? items = null)
        => ReviewDecision.Create(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), "sub-1", "Aurora Reviewer",
            kind, justification, claimantExplanation, items, ai, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Approving_in_agreement_with_a_valid_AI_approve_needs_no_justification()
    {
        var decision = Decide(ReviewDecisionKind.Approve, Valid(AiDecision.Approve), claimantExplanation: ClaimantText);

        decision.OverridesAi.ShouldBeFalse();
        decision.Justification.ShouldBeNull();
    }

    [Fact]
    public void Overriding_a_valid_AI_decision_requires_a_justification()
    {
        Should.Throw<ReviewDecisionValidationException>(
                () => Decide(ReviewDecisionKind.Approve, Valid(AiDecision.Reject), claimantExplanation: ClaimantText))
            .Field.ShouldBe("justification");

        Decide(ReviewDecisionKind.Approve, Valid(AiDecision.Reject), Reason, ClaimantText).OverridesAi.ShouldBeTrue();
    }

    [Fact]
    public void Rejecting_always_requires_a_justification_even_in_agreement()
    {
        Should.Throw<ReviewDecisionValidationException>(
            () => Decide(ReviewDecisionKind.Reject, Valid(AiDecision.Reject), claimantExplanation: ClaimantText));

        var decision = Decide(ReviewDecisionKind.Reject, Valid(AiDecision.Reject), Reason, ClaimantText);
        decision.OverridesAi.ShouldBeFalse();
    }

    [Theory]
    [InlineData(AiDecision.HumanReview)]
    [InlineData(AiDecision.RequestMoreInformation)]
    public void Without_a_valid_approve_or_reject_there_is_nothing_to_override(AiDecision aiDecision)
        => Decide(ReviewDecisionKind.Approve, Valid(aiDecision), claimantExplanation: ClaimantText).OverridesAi.ShouldBeFalse();

    [Fact]
    public void An_invalid_or_missing_recommendation_is_never_overridden()
    {
        Decide(ReviewDecisionKind.Approve, Invalid(), claimantExplanation: ClaimantText).OverridesAi.ShouldBeFalse();
        Decide(ReviewDecisionKind.Approve, null, claimantExplanation: ClaimantText).OverridesAi.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Too short")]
    public void Approve_and_reject_require_a_claimant_explanation_of_20_to_1500_characters(string? message)
        => Should.Throw<ReviewDecisionValidationException>(
                () => Decide(ReviewDecisionKind.Approve, Valid(AiDecision.Approve), claimantExplanation: message))
            .Field.ShouldBe("claimantExplanation");

    [Fact]
    public void Justification_length_is_bounded()
        => Should.Throw<ReviewDecisionValidationException>(
            () => Decide(ReviewDecisionKind.Reject, null, "short", ClaimantText));

    [Fact]
    public void A_request_for_information_lists_items_and_has_no_claimant_explanation()
    {
        var items = new[] { RequestedItem.Create("photo_of_serial_label", "Please send a photo of the serial label.") };

        Decide(ReviewDecisionKind.RequestInformation, Valid(AiDecision.Approve), Reason, items: items).RequestedItems.Count.ShouldBe(1);
        Should.Throw<ReviewDecisionValidationException>(
                () => Decide(ReviewDecisionKind.RequestInformation, null, claimantExplanation: ClaimantText, items: items))
            .Field.ShouldBe("claimantExplanation");
        Should.Throw<ReviewDecisionValidationException>(() => Decide(ReviewDecisionKind.RequestInformation, null))
            .Field.ShouldBe("requestedItems");
    }

    [Fact]
    public void Recommendation_has_no_public_setters()
        => typeof(Recommendation)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .ShouldBeEmpty();

    [Fact]
    public void Operator_only_security_events_carry_no_tenant_and_others_need_one()
    {
        SecurityEvent.Create(Guid.CreateVersion7(), null, DateTimeOffset.UnixEpoch, SecurityEventKind.UnknownChannel, "claimant channel", "evil.localhost", null, null)
            .TenantId.ShouldBeNull();
        Should.Throw<ArgumentException>(() => SecurityEvent.Create(
            Guid.CreateVersion7(), Tenant, DateTimeOffset.UnixEpoch, SecurityEventKind.CrossTenantAccessDenied, "sub-1", "x", null, null));
        Should.Throw<ArgumentException>(() => SecurityEvent.Create(
            Guid.CreateVersion7(), null, DateTimeOffset.UnixEpoch, SecurityEventKind.AccessDenied, "sub-1", "x", null, null));
    }

    [Fact]
    public void Run_steps_only_move_forward_and_completion_records_the_disposition()
    {
        var run = AdjudicationRun.Start(Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), 1, "corr", DateTimeOffset.UnixEpoch);

        run.AdvanceTo(RunStep.Policy);
        Should.Throw<InvalidOperationException>(() => run.AdvanceTo(RunStep.Evidence));

        run.RecordAiFailure("Decision agent timed out");
        run.Complete(Disposition.HumanReview, DateTimeOffset.UnixEpoch.AddMinutes(1));

        run.Status.ShouldBe(RunStatus.Completed);
        run.CurrentStep.ShouldBe(RunStep.Done);
        run.Disposition.ShouldBe(Disposition.HumanReview);
        run.FailureReason.ShouldBe("Decision agent timed out");
        Should.Throw<InvalidOperationException>(() => run.AdvanceTo(RunStep.Done));
    }

    [Fact]
    public void Risk_is_low_exactly_when_there_are_no_signals()
    {
        var signal = new RiskSignal(RiskSignalCode.DuplicateSerialClaim, RiskSignalSource.Deterministic, RiskSeverity.Medium, "open claim", []);

        Should.Throw<ArgumentException>(() => RiskAssessment.Create(Guid.CreateVersion7(), Tenant, RiskAssessmentStage.Full, 25, RiskLevel.Low, [signal]));
        Should.Throw<ArgumentException>(() => RiskAssessment.Create(Guid.CreateVersion7(), Tenant, RiskAssessmentStage.Full, 0, RiskLevel.Medium, []));
        RiskAssessment.Create(Guid.CreateVersion7(), Tenant, RiskAssessmentStage.Full, 25, RiskLevel.Medium, [signal]).Signals.Count.ShouldBe(1);
    }

    [Fact]
    public void Automatic_finalization_cannot_be_recorded_with_a_failed_check()
    {
        var failed = new GuardrailCheck(GuardrailCheckCode.RiskLow, "risk", false, "no signals", "1 signal", null);

        Should.Throw<ArgumentException>(() => GuardrailEvaluation.Create(
            Guid.CreateVersion7(), Tenant, [failed], Disposition.AutoApprove, [], null, DateTimeOffset.UnixEpoch));
        Should.Throw<ArgumentException>(() => GuardrailEvaluation.Create(
            Guid.CreateVersion7(), Tenant, [failed], Disposition.HumanReview, [], null, DateTimeOffset.UnixEpoch));
        GuardrailEvaluation.Create(
                Guid.CreateVersion7(), Tenant, [failed], Disposition.HumanReview, [EscalationReason.RiskMedium], null, DateTimeOffset.UnixEpoch)
            .Reasons.ShouldBe([EscalationReason.RiskMedium]);
    }

    [Fact]
    public void Retrieved_policy_references_must_be_POL_ids()
        => Should.Throw<ArgumentException>(() => RetrievedPolicyRef.Create(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), "GLB-1", Guid.CreateVersion7(), Guid.CreateVersion7(), "AUR-WP-1",
            Warranty.Domain.Policies.ClauseType.Coverage, null, "Aurora Limited Warranty", 1, new DateOnly(2025, 1, 1), null, 0.8f));
}
