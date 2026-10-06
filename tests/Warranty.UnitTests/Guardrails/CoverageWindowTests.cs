using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// Deterministic coverage window (data-model.md "Coverage window rule"): <c>coverage_end = purchase_date +
/// months(component-specific or standard months for region)</c>, covered iff <c>claim_date ≤ coverage_end</c>.
/// </summary>
public sealed class CoverageWindowTests
{
    /// <summary>AUR-WP v1: 12 months NA / 24 months EU, battery 6 months.</summary>
    private static CoverageTerms AuroraV1Terms(int batteryMonths = 6) => new(
        new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 },
        new Dictionary<string, int> { ["battery"] = batteryMonths },
        AccidentalDamageTerms.NotCovered,
        [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage, ExclusionCode.CosmeticDamage, ExclusionCode.UnauthorizedRepair]);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Theory]
    [InlineData(Region.NA, 2026, 3, 10)]
    [InlineData(Region.EU, 2027, 3, 10)]
    public void Standard_window_uses_the_months_of_the_claim_region(Region region, int endYear, int endMonth, int endDay)
    {
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), region, null, D(2025, 3, 10), D(2026, 1, 15));

        result.Outcome.ShouldBe(CoverageWindowOutcome.Determined);
        result.CoverageEndDate.ShouldBe(D(endYear, endMonth, endDay));
        result.WithinStandardCoverage.ShouldBe(true);
        result.WithinComponentCoverage.ShouldBe(true);
    }

    [Theory]
    [InlineData(Region.NA, false)]
    [InlineData(Region.EU, true)]
    public void Same_dates_can_be_covered_in_one_region_and_expired_in_another(Region region, bool covered)
    {
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), region, null, D(2025, 3, 10), D(2026, 6, 1));

        result.WithinStandardCoverage.ShouldBe(covered);
        result.WithinComponentCoverage.ShouldBe(covered);
    }

    [Fact]
    public void Battery_claim_after_component_months_is_outside_component_but_inside_standard_coverage()
    {
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, "BATTERY", D(2025, 3, 10), D(2025, 11, 1));

        result.Outcome.ShouldBe(CoverageWindowOutcome.Determined);
        result.CoverageEndDate.ShouldBe(D(2025, 9, 10));
        result.WithinStandardCoverage.ShouldBe(true);
        result.WithinComponentCoverage.ShouldBe(false);
    }

    [Fact]
    public void Battery_claim_within_component_months_is_covered()
    {
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.EU, "BATTERY", D(2025, 3, 10), D(2025, 8, 1));

        result.CoverageEndDate.ShouldBe(D(2025, 9, 10));
        result.WithinStandardCoverage.ShouldBe(true);
        result.WithinComponentCoverage.ShouldBe(true);
    }

    [Fact]
    public void Battery_months_follow_the_terms_of_the_version_passed_in()
    {
        // AUR-WP v2 raises the battery period to 12 months.
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(batteryMonths: 12), Region.NA, "BATTERY", D(2026, 7, 15), D(2027, 3, 1));

        result.CoverageEndDate.ShouldBe(D(2027, 7, 15));
        result.WithinComponentCoverage.ShouldBe(true);
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("Battery")]
    [InlineData(" BATTERY ")]
    public void Component_is_matched_case_insensitively(string component)
        => CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, component, D(2025, 3, 10), D(2025, 4, 1))
            .CoverageEndDate.ShouldBe(D(2025, 9, 10));

    [Theory]
    [InlineData(null)]
    [InlineData("SCREEN")]
    [InlineData("UNKNOWN")]
    public void Component_without_specific_months_uses_the_standard_window(string? component)
    {
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, component, D(2025, 3, 10), D(2025, 11, 1));

        result.CoverageEndDate.ShouldBe(D(2026, 3, 10));
        result.WithinStandardCoverage.ShouldBe(true);
        result.WithinComponentCoverage.ShouldBe(true);
    }

    [Fact]
    public void Component_months_longer_than_standard_extend_the_component_window()
    {
        var terms = AuroraV1Terms(batteryMonths: 36);

        var result = CoverageWindowCalculator.Calculate(terms, Region.NA, "BATTERY", D(2025, 3, 10), D(2026, 11, 1));

        result.CoverageEndDate.ShouldBe(D(2028, 3, 10));
        result.WithinStandardCoverage.ShouldBe(false);
        result.WithinComponentCoverage.ShouldBe(true);
    }

    [Theory]
    [InlineData(2025, 8, 31, null, 2026, 8, 31)]      // 12 months NA, no clamping needed
    [InlineData(2025, 8, 31, "BATTERY", 2026, 2, 28)] // 6 months lands in February
    [InlineData(2023, 8, 31, "BATTERY", 2024, 2, 29)] // ... of a leap year
    [InlineData(2025, 3, 31, "BATTERY", 2025, 9, 30)] // 30-day month
    [InlineData(2024, 2, 29, null, 2025, 2, 28)]      // leap day + 12 months
    public void Month_end_purchase_dates_clamp_to_the_last_day_of_the_target_month(
        int purchaseYear, int purchaseMonth, int purchaseDay, string? component, int endYear, int endMonth, int endDay)
    {
        var purchase = D(purchaseYear, purchaseMonth, purchaseDay);

        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, component, purchase, purchase.AddDays(1));

        result.CoverageEndDate.ShouldBe(D(endYear, endMonth, endDay));
    }

    [Theory]
    [InlineData(null, 2026, 3, 10)]
    [InlineData("BATTERY", 2025, 9, 10)]
    public void Claim_on_the_last_covered_day_is_covered(string? component, int endYear, int endMonth, int endDay)
    {
        var lastDay = D(endYear, endMonth, endDay);

        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, component, D(2025, 3, 10), lastDay);

        result.CoverageEndDate.ShouldBe(lastDay);
        result.WithinComponentCoverage.ShouldBe(true);
    }

    [Theory]
    [InlineData(null, 2026, 3, 11)]
    [InlineData("BATTERY", 2025, 9, 11)]
    public void Claim_the_day_after_the_last_covered_day_is_not_covered(string? component, int year, int month, int day)
        => CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, component, D(2025, 3, 10), D(year, month, day))
            .WithinComponentCoverage.ShouldBe(false);

    [Fact]
    public void Month_end_purchase_is_covered_on_the_clamped_last_day_and_not_after()
    {
        var purchase = D(2025, 8, 31);

        CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, "BATTERY", purchase, D(2026, 2, 28))
            .WithinComponentCoverage.ShouldBe(true);
        CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, "BATTERY", purchase, D(2026, 3, 1))
            .WithinComponentCoverage.ShouldBe(false);
    }

    [Fact]
    public void Claim_on_the_purchase_date_is_covered()
    {
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, "BATTERY", D(2025, 3, 10), D(2025, 3, 10));

        result.WithinStandardCoverage.ShouldBe(true);
        result.WithinComponentCoverage.ShouldBe(true);
    }

    [Fact]
    public void No_applicable_policy_leaves_the_window_undetermined()
    {
        var result = CoverageWindowCalculator.Calculate(null, Region.NA, "BATTERY", D(2025, 3, 10), D(2025, 4, 1));

        result.Outcome.ShouldBe(CoverageWindowOutcome.NoApplicablePolicy);
        result.CoverageEndDate.ShouldBeNull();
        result.WithinStandardCoverage.ShouldBeNull();
        result.WithinComponentCoverage.ShouldBeNull();
    }

    [Fact]
    public void Terms_without_months_for_the_claim_region_leave_the_window_undetermined()
    {
        var naOnly = AuroraV1Terms() with { StandardCoverageMonths = new Dictionary<Region, int> { [Region.NA] = 12 } };

        var result = CoverageWindowCalculator.Calculate(naOnly, Region.EU, null, D(2025, 3, 10), D(2025, 4, 1));

        result.Outcome.ShouldBe(CoverageWindowOutcome.NoApplicablePolicy);
        result.CoverageEndDate.ShouldBeNull();
        result.WithinStandardCoverage.ShouldBeNull();
        result.WithinComponentCoverage.ShouldBeNull();
        (result.AccidentalWindowEndDate, result.WithinAccidentalWindow).ShouldBe((null, null));
    }

    // ---- Accidental-damage allowance window (T077) ---------------------------------------------------

    /// <summary>BOR-WP v1: 24 months in all regions, battery 12 months, one accidental-damage incident within 12 months.</summary>
    private static CoverageTerms BorealisV1Terms() => new(
        new Dictionary<Region, int> { [Region.NA] = 24, [Region.EU] = 24 },
        new Dictionary<string, int> { ["battery"] = 12 },
        new AccidentalDamageTerms(true, 12, 1),
        [ExclusionCode.LiquidDamage, ExclusionCode.UnauthorizedRepair]);

    [Theory]
    [InlineData(2026, 3, 10, true)]
    [InlineData(2026, 3, 11, false)]
    [InlineData(2025, 9, 1, true)]
    public void The_accidental_window_ends_windowMonths_after_purchase_inclusive(int year, int month, int day, bool within)
    {
        var result = CoverageWindowCalculator.Calculate(BorealisV1Terms(), Region.NA, "SCREEN", D(2025, 3, 10), D(year, month, day));

        result.AccidentalWindowEndDate.ShouldBe(D(2026, 3, 10));
        result.WithinAccidentalWindow.ShouldBe(within);
    }

    [Fact]
    public void The_accidental_window_is_independent_of_the_standard_and_component_windows()
    {
        // 14 months after purchase: inside the 24-month defect window, outside the 12-month accidental allowance.
        var result = CoverageWindowCalculator.Calculate(BorealisV1Terms(), Region.EU, null, D(2025, 1, 31), D(2026, 3, 31));

        (result.CoverageEndDate, result.WithinComponentCoverage).ShouldBe((D(2027, 1, 31), true));
        (result.AccidentalWindowEndDate, result.WithinAccidentalWindow).ShouldBe((D(2026, 1, 31), false));
    }

    [Fact]
    public void Terms_that_do_not_cover_accidental_damage_have_no_accidental_window()
    {
        var result = CoverageWindowCalculator.Calculate(AuroraV1Terms(), Region.NA, "SCREEN", D(2025, 3, 10), D(2025, 4, 1));

        result.Outcome.ShouldBe(CoverageWindowOutcome.Determined);
        (result.AccidentalWindowEndDate, result.WithinAccidentalWindow).ShouldBe((null, null));
    }

    [Fact]
    public void The_accidental_window_end_follows_month_end_clamping()
    {
        var result = CoverageWindowCalculator.Calculate(
            BorealisV1Terms() with { AccidentalDamage = new AccidentalDamageTerms(true, 1, 1) }, Region.NA, null, D(2025, 1, 31), D(2025, 2, 28));

        (result.AccidentalWindowEndDate, result.WithinAccidentalWindow).ShouldBe((D(2025, 2, 28), true));
    }
}
