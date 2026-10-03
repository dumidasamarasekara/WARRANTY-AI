using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.UnitTests.Domain;

public sealed class PolicyModelTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private static CoverageTerms AuroraTerms(int batteryMonths = 6) => new(
        new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 },
        new Dictionary<string, int> { ["battery"] = batteryMonths },
        AccidentalDamageTerms.NotCovered,
        [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage, ExclusionCode.CosmeticDamage, ExclusionCode.UnauthorizedRepair]);

    private static PolicyVersion Version(
        DateOnly from,
        DateOnly? to,
        IEnumerable<string>? categories = null,
        CoverageTerms? terms = null,
        int version = 1)
        => PolicyVersion.Create(
            Guid.CreateVersion7(), TenantId, Guid.CreateVersion7(), version, from, to,
            [Region.NA, Region.EU], categories ?? [], terms ?? AuroraTerms(), "tenant-aurora/policies/AUR-WP-v1.md", "abc123");

    [Fact]
    public void Version_applies_on_purchase_dates_within_inclusive_bounds()
    {
        var v1 = Version(new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30));

        v1.AppliesTo(new DateOnly(2025, 1, 1), Region.NA, "tablet").ShouldBeTrue();
        v1.AppliesTo(new DateOnly(2026, 3, 1), Region.EU, "tablet").ShouldBeTrue();
        v1.AppliesTo(new DateOnly(2026, 6, 30), Region.NA, "tablet").ShouldBeTrue();
        v1.AppliesTo(new DateOnly(2026, 7, 1), Region.NA, "tablet").ShouldBeFalse();
        v1.AppliesTo(new DateOnly(2024, 12, 31), Region.NA, "tablet").ShouldBeFalse();
    }

    [Fact]
    public void Open_ended_version_applies_to_any_later_purchase()
        => Version(new DateOnly(2026, 7, 1), null).AppliesTo(new DateOnly(2030, 1, 1), Region.NA, "tablet").ShouldBeTrue();

    [Fact]
    public void Version_restricted_to_categories_applies_only_to_them()
    {
        var version = Version(new DateOnly(2024, 1, 1), null, categories: ["Major-Appliance"]);

        version.AppliesTo(new DateOnly(2025, 1, 1), Region.NA, "major-appliance").ShouldBeTrue();
        version.AppliesTo(new DateOnly(2025, 1, 1), Region.NA, "tablet").ShouldBeFalse();
        version.AppliesTo(new DateOnly(2025, 1, 1), Region.NA, null).ShouldBeFalse();
    }

    [Fact]
    public void Versions_overlapping_in_time_are_detected()
    {
        var v1 = Version(new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30));
        var v2 = Version(new DateOnly(2026, 7, 1), null, version: 2);
        var overlapping = Version(new DateOnly(2026, 6, 30), null, version: 3);

        v1.OverlapsInTime(v2).ShouldBeFalse();
        v1.OverlapsInTime(overlapping).ShouldBeTrue();
        v2.OverlapsInTime(overlapping).ShouldBeTrue();
    }

    [Fact]
    public void Version_rejects_an_end_before_its_start()
        => Should.Throw<ArgumentException>(() => Version(new DateOnly(2026, 1, 1), new DateOnly(2025, 12, 31)));

    [Fact]
    public void Terms_must_cover_every_region_of_the_version()
    {
        var naOnly = AuroraTerms() with { StandardCoverageMonths = new Dictionary<Region, int> { [Region.NA] = 12 } };

        Should.Throw<ArgumentException>(() => Version(new DateOnly(2025, 1, 1), null, terms: naOnly));
    }

    [Fact]
    public void Terms_reject_covered_accidental_damage_that_is_also_excluded()
    {
        var contradictory = AuroraTerms() with { AccidentalDamage = new AccidentalDamageTerms(true, 12, 1) };

        Should.Throw<ArgumentException>(() => contradictory.Validate([Region.NA, Region.EU]));
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(true, 12, 0)]
    [InlineData(false, 12, 0)]
    public void Accidental_damage_terms_must_be_consistent(bool covered, int windowMonths, int maxIncidents)
        => Should.Throw<ArgumentException>(() => new AccidentalDamageTerms(covered, windowMonths, maxIncidents).Validate());

    [Fact]
    public void Exclusion_clause_requires_a_code_listed_in_the_version_terms()
    {
        var version = Version(new DateOnly(2025, 1, 1), null);

        var clause = PolicyClause.Create(
            Guid.CreateVersion7(), version, "AUR-WP-3.2", ClauseType.Exclusion, ExclusionCode.AccidentalDamage, "Exclusions", "Accidental damage is not covered.");

        clause.ExclusionCode.ShouldBe(ExclusionCode.AccidentalDamage);
        clause.TenantId.ShouldBe(TenantId);
        Should.Throw<ArgumentException>(() => PolicyClause.Create(
            Guid.CreateVersion7(), version, "AUR-WP-3.3", ClauseType.Exclusion, null, "Exclusions", "Text"));
    }

    [Fact]
    public void Exclusion_clause_with_a_code_the_version_does_not_exclude_is_rejected()
    {
        var borealisTerms = AuroraTerms() with
        {
            AccidentalDamage = new AccidentalDamageTerms(true, 12, 1),
            Exclusions = [ExclusionCode.LiquidDamage, ExclusionCode.UnauthorizedRepair],
        };
        var version = Version(new DateOnly(2024, 1, 1), null, terms: borealisTerms);

        Should.Throw<ArgumentException>(() => PolicyClause.Create(
            Guid.CreateVersion7(), version, "BOR-WP-4.1", ClauseType.Exclusion, ExclusionCode.AccidentalDamage, "Exclusions", "Text"));
    }

    [Fact]
    public void Non_exclusion_clauses_must_not_carry_an_exclusion_code()
        => Should.Throw<ArgumentException>(() => PolicyClause.Create(
            Guid.CreateVersion7(), Version(new DateOnly(2025, 1, 1), null), "AUR-WP-2.1", ClauseType.Period,
            ExclusionCode.LiquidDamage, "Coverage period", "Text"));
}
