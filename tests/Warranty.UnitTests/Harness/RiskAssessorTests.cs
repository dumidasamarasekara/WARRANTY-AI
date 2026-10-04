using Warranty.AI.Harness.Agents.Risk;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.UnitTests.Harness;

/// <summary>Risk level and score derivation (FR-017, research R23).</summary>
public sealed class RiskAssessorTests
{
    private const int DefaultThreshold = 60;
    private const string Pending = "Pending T064";

    [Fact(Skip = Pending)]
    public void No_signal_is_low_with_score_zero()
    {
        var result = RiskAssessor.Compute([], AiRiskReading.None, DefaultThreshold);

        result.Level.ShouldBe(RiskLevel.Low);
        result.Score.ShouldBe(0);
        result.Signals.ShouldBeEmpty();
    }

    [Theory(Skip = Pending)]
    [InlineData(RiskSignalCode.ProductNotInCatalog)]
    [InlineData(RiskSignalCode.DuplicateSerialClaim)]
    [InlineData(RiskSignalCode.EvidenceReused)]
    [InlineData(RiskSignalCode.PurchaseDateAnomaly)]
    [InlineData(RiskSignalCode.SourceInconsistency)]
    [InlineData(RiskSignalCode.SerialMismatchPhoto)]
    [InlineData(RiskSignalCode.ManipulationAttempt)]
    public void Any_single_deterministic_signal_is_medium(RiskSignalCode code)
    {
        var result = RiskAssessor.Compute([Deterministic(code)], AiRiskReading.None, DefaultThreshold);

        result.Level.ShouldBe(RiskLevel.Medium);
        result.Signals.Single().Code.ShouldBe(code);
    }

    [Theory(Skip = Pending)]
    [InlineData(RiskSignalCode.DamageInconsistentWithDescription)]
    [InlineData(RiskSignalCode.Other)]
    [InlineData(RiskSignalCode.SourceInconsistency)]
    [InlineData(RiskSignalCode.ManipulationAttempt)]
    public void Any_single_ai_signal_is_medium_with_medium_weight(RiskSignalCode code)
    {
        // The AI's own severity is ignored: AI-sourced signals score as Medium.
        var result = RiskAssessor.Compute([], Ai(code, RiskSeverity.High), DefaultThreshold);

        result.Level.ShouldBe(RiskLevel.Medium);
        result.Score.ShouldBe(25);
        result.Signals.Single().Code.ShouldBe(code);
    }

    [Theory(Skip = Pending)]
    [InlineData(RiskSeverity.Low, 10)]
    [InlineData(RiskSeverity.Medium, 25)]
    [InlineData(RiskSeverity.High, 40)]
    public void Severity_weights_are_10_25_40(RiskSeverity severity, int weight)
        => RiskAssessor.WeightOf(severity).ShouldBe(weight);

    [Theory(Skip = Pending)]
    [InlineData(RiskSignalCode.ManipulationAttempt, RiskSeverity.High)]
    [InlineData(RiskSignalCode.EvidenceReused, RiskSeverity.High)]
    [InlineData(RiskSignalCode.SerialMismatchPhoto, RiskSeverity.High)]
    [InlineData(RiskSignalCode.DuplicateSerialClaim, RiskSeverity.Medium)]
    [InlineData(RiskSignalCode.ProductNotInCatalog, RiskSeverity.Medium)]
    [InlineData(RiskSignalCode.SourceInconsistency, RiskSeverity.Medium)]
    [InlineData(RiskSignalCode.PurchaseDateAnomaly, RiskSeverity.Medium)]
    public void Deterministic_signals_have_fixed_severities(RiskSignalCode code, RiskSeverity severity)
    {
        RiskAssessor.SeverityOf(code, RiskSignalSource.Deterministic).ShouldBe(severity);
        RiskAssessor.SeverityOf(code, RiskSignalSource.Ai).ShouldBe(RiskSeverity.Medium);
    }

    [Fact(Skip = Pending)]
    public void Weights_are_summed()
    {
        var result = RiskAssessor.Compute(
            [Deterministic(RiskSignalCode.EvidenceReused), Deterministic(RiskSignalCode.DuplicateSerialClaim)],
            Ai(RiskSignalCode.DamageInconsistentWithDescription),
            riskHighThreshold: 100);

        result.Score.ShouldBe(40 + 25 + 25);
        result.Level.ShouldBe(RiskLevel.Medium);
        result.Signals.Count.ShouldBe(3);
    }

    [Fact(Skip = Pending)]
    public void Score_is_capped_at_100()
    {
        var result = RiskAssessor.Compute(
            [
                Deterministic(RiskSignalCode.ManipulationAttempt),
                Deterministic(RiskSignalCode.EvidenceReused),
                Deterministic(RiskSignalCode.SerialMismatchPhoto),
                Deterministic(RiskSignalCode.DuplicateSerialClaim),
            ],
            AiRiskReading.None,
            DefaultThreshold);

        result.Score.ShouldBe(100);
        result.Level.ShouldBe(RiskLevel.High);
    }

    [Theory(Skip = Pending)]
    [InlineData(0, false, RiskLevel.Low)]
    [InlineData(25, true, RiskLevel.Medium)]
    [InlineData(59, true, RiskLevel.Medium)]
    [InlineData(60, true, RiskLevel.High)]
    [InlineData(100, true, RiskLevel.High)]
    public void Level_is_high_exactly_at_the_threshold(int score, bool anySignal, RiskLevel level)
        => RiskAssessor.LevelFor(score, anySignal, DefaultThreshold).ShouldBe(level);

    [Theory(Skip = Pending)]
    [InlineData(65, RiskLevel.High)]
    [InlineData(66, RiskLevel.Medium)]
    public void Computed_level_uses_the_tenant_threshold(int threshold, RiskLevel level)
    {
        // 40 + 25 = 65.
        var result = RiskAssessor.Compute(
            [Deterministic(RiskSignalCode.EvidenceReused), Deterministic(RiskSignalCode.DuplicateSerialClaim)],
            AiRiskReading.None,
            threshold);

        result.Score.ShouldBe(65);
        result.Level.ShouldBe(level);
    }

    [Fact(Skip = Pending)]
    public void A_code_raised_by_both_sources_is_counted_once_with_the_deterministic_severity()
    {
        var result = RiskAssessor.Compute(
            [Deterministic(RiskSignalCode.EvidenceReused)],
            Ai(RiskSignalCode.EvidenceReused),
            DefaultThreshold);

        result.Score.ShouldBe(40);
        var signal = result.Signals.ShouldHaveSingleItem();
        signal.Code.ShouldBe(RiskSignalCode.EvidenceReused);
        signal.Source.ShouldBe(RiskSignalSource.Deterministic);
        signal.Severity.ShouldBe(RiskSeverity.High);
    }

    [Fact(Skip = Pending)]
    public void An_ai_duplicate_of_a_medium_deterministic_code_does_not_raise_its_weight()
    {
        var result = RiskAssessor.Compute(
            [Deterministic(RiskSignalCode.SourceInconsistency)],
            Ai(RiskSignalCode.SourceInconsistency, RiskSeverity.High),
            DefaultThreshold);

        result.Score.ShouldBe(25);
        result.Signals.ShouldHaveSingleItem().Severity.ShouldBe(RiskSeverity.Medium);
    }

    [Theory(Skip = Pending)]
    [InlineData(RiskLevel.High)]
    [InlineData(RiskLevel.Medium)]
    public void The_model_level_does_not_raise_risk_without_signals(RiskLevel modelLevel)
    {
        var result = RiskAssessor.Compute([], new AiRiskReading(modelLevel, []), DefaultThreshold);

        result.Level.ShouldBe(RiskLevel.Low);
        result.Score.ShouldBe(0);
    }

    [Fact(Skip = Pending)]
    public void The_model_level_does_not_lower_risk_with_signals()
    {
        var oneSignal = RiskAssessor.Compute(
            [], new AiRiskReading(RiskLevel.Low, [AiSignal(RiskSignalCode.Other)]), DefaultThreshold);
        var highScore = RiskAssessor.Compute(
            [Deterministic(RiskSignalCode.ManipulationAttempt), Deterministic(RiskSignalCode.EvidenceReused)],
            new AiRiskReading(RiskLevel.Medium, []),
            DefaultThreshold);

        oneSignal.Level.ShouldBe(RiskLevel.Medium);
        highScore.Level.ShouldBe(RiskLevel.High);
    }

    // Fixed deterministic severities from research R23, stated here independently of the implementation.
    private static RiskSignal Deterministic(RiskSignalCode code)
    {
        var severity = code is RiskSignalCode.ManipulationAttempt or RiskSignalCode.EvidenceReused or RiskSignalCode.SerialMismatchPhoto
            ? RiskSeverity.High
            : RiskSeverity.Medium;
        return new(code, RiskSignalSource.Deterministic, severity, $"{code} found", []);
    }

    private static RiskSignal AiSignal(RiskSignalCode code, RiskSeverity severity = RiskSeverity.Medium)
        => new(code, RiskSignalSource.Ai, severity, $"Model reported {code}", ["EV-1"]);

    private static AiRiskReading Ai(RiskSignalCode code, RiskSeverity severity = RiskSeverity.Medium)
        => new(RiskLevel.Low, [AiSignal(code, severity)]);
}
