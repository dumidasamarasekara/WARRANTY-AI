using NSubstitute;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using static Warranty.UnitTests.Harness.Tools.ToolTestKit;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary><c>warranty_lookup</c> (Policy): version selection plus the deterministic coverage window.</summary>
public sealed class WarrantyLookupToolTests
{
    private static readonly Guid PolicyId = Guid.Parse("0199a000-0000-7000-8000-0000000000f1");

    // Claim: purchased 2026-01-15, claimed 2026-09-30 (8.5 months later).
    private static readonly CoverageTerms Terms = new(
        new Dictionary<Region, int> { [Region.NA] = 6, [Region.EU] = 24 },
        new Dictionary<string, int> { ["battery"] = 6 },
        new AccidentalDamageTerms(true, 12, 1),
        [ExclusionCode.LiquidDamage, ExclusionCode.CosmeticDamage]);

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();

    public WarrantyLookupToolTests()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(NewClaim());
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>()).Returns(NewProduct());
        _policies.GetPolicyAsync(PolicyId, Arg.Any<CancellationToken>()).Returns(WarrantyPolicy.Create(PolicyId, Aurora, "AUR-WP", "Aurora Limited Warranty"));
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Version(1, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)),
            Version(2, new DateOnly(2026, 1, 1), null),
        ]);
    }

    private WarrantyLookupTool Tool => new(_claims, _catalog, _policies);

    [Fact]
    public void It_takes_only_a_component_and_is_for_the_policy_agent()
    {
        ShouldBeStrictReadOnlyTool(Tool, "warranty_lookup", AgentNames.Policy);
        AllPropertyNames(Tool.Descriptor.InputSchema).ShouldBe(["component"]);
        Tool.Descriptor.InputSchema.GetProperty("properties").GetProperty("component").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(WarrantyLookupTool.Components);
    }

    [Fact]
    public async Task The_version_in_effect_on_the_purchase_date_applies_with_its_terms_for_the_claims_region()
    {
        var result = await Tool.InvokeAsync(Args("""{"component":"SCREEN"}"""), Context(AgentNames.Policy), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        var content = result.Content;
        content.GetProperty("outcome").GetString().ShouldBe("Ok");
        content.GetProperty("region").GetString().ShouldBe("EU");
        var policy = content.GetProperty("policy");
        (policy.GetProperty("code").GetString(), policy.GetProperty("version").GetInt32(), policy.GetProperty("effectiveFrom").GetString())
            .ShouldBe(("AUR-WP", 2, "2026-01-01"));
        policy.TryGetProperty("policyVersionId", out _).ShouldBeFalse("database IDs are never shown to a model");
        var terms = content.GetProperty("terms");
        terms.GetProperty("standardCoverageMonths").GetInt32().ShouldBe(24);
        terms.GetProperty("componentCoverageMonths").GetProperty("battery").GetInt32().ShouldBe(6);
        terms.GetProperty("exclusions").EnumerateArray().Select(e => e.GetString()).ShouldBe(["LIQUID_DAMAGE", "COSMETIC_DAMAGE"]);
        var coverage = content.GetProperty("coverage");
        coverage.GetProperty("coverageEndDate").GetString().ShouldBe("2028-01-15");
        coverage.GetProperty("withinStandardCoverage").GetBoolean().ShouldBeTrue();
        coverage.GetProperty("withinComponentCoverage").GetBoolean().ShouldBeTrue();
        coverage.GetProperty("accidentalWindowEndDate").GetString().ShouldBe("2027-01-15");
        coverage.GetProperty("withinAccidentalWindow").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_component_with_its_own_months_uses_them()
    {
        var result = await Tool.LookupAsync(ClaimId, "BATTERY", TestContext.Current.CancellationToken);

        result.Coverage.ShouldBe(new WarrantyCoverageInfo(new DateOnly(2026, 7, 15), true, false, new DateOnly(2027, 1, 15), true));
        result.Policy!.PolicyVersionId.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("OTHER")]
    [InlineData("UNKNOWN")]
    public async Task Other_and_unknown_components_use_the_standard_months(string component)
    {
        var result = await Tool.LookupAsync(ClaimId, component, TestContext.Current.CancellationToken);

        result.Coverage!.CoverageEndDate.ShouldBe(new DateOnly(2028, 1, 15));
    }

    [Fact]
    public async Task The_claims_region_decides_the_standard_months()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(NewClaim(region: Region.NA));

        var result = await Tool.LookupAsync(ClaimId, "SCREEN", TestContext.Current.CancellationToken);

        (result.Terms!.StandardCoverageMonths, result.Coverage!.CoverageEndDate, result.Coverage.WithinComponentCoverage)
            .ShouldBe((6, new DateOnly(2026, 7, 15), false));
    }

    [Fact]
    public async Task Accidental_damage_that_is_not_covered_has_no_window()
    {
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns(
            [Version(2, new DateOnly(2026, 1, 1), null, Terms with { AccidentalDamage = AccidentalDamageTerms.NotCovered })]);

        var result = await Tool.LookupAsync(ClaimId, "SCREEN", TestContext.Current.CancellationToken);

        (result.Coverage!.AccidentalWindowEndDate, result.Coverage.WithinAccidentalWindow).ShouldBe((null, null));
    }

    [Fact]
    public async Task No_version_in_effect_is_no_applicable_policy_and_nothing_is_computed()
    {
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns([Version(1, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31))]);

        var result = await Tool.LookupAsync(ClaimId, "SCREEN", TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(PolicyVersionOutcome.NoApplicablePolicy);
        (result.Policy, result.Terms, result.Coverage).ShouldBe((null, null, null));
    }

    [Fact]
    public async Task A_version_for_other_categories_does_not_apply()
    {
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns([Version(2, new DateOnly(2026, 1, 1), null, categories: ["laptop"])]);

        (await Tool.LookupAsync(ClaimId, "SCREEN", TestContext.Current.CancellationToken)).Outcome.ShouldBe(PolicyVersionOutcome.NoApplicablePolicy);
    }

    [Fact]
    public async Task Two_versions_in_effect_are_ambiguous()
    {
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns(
            [Version(2, new DateOnly(2026, 1, 1), null), Version(3, new DateOnly(2025, 6, 1), null, categories: ["tablet"])]);

        var result = await Tool.LookupAsync(ClaimId, "SCREEN", TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(PolicyVersionOutcome.AmbiguousPolicyVersion);
        result.Coverage.ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_region_no_policy_applies()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(NewClaim(region: null));

        var result = await Tool.LookupAsync(ClaimId, "SCREEN", TestContext.Current.CancellationToken);

        (result.Outcome, result.Region).ShouldBe((PolicyVersionOutcome.NoApplicablePolicy, (string?)null));
    }

    private static PolicyVersion Version(int number, DateOnly from, DateOnly? to, CoverageTerms? terms = null, string[]? categories = null)
        => PolicyVersion.Create(
            Guid.NewGuid(), Aurora, PolicyId, number, from, to, [Region.NA, Region.EU], categories ?? [], terms ?? Terms,
            $"tenant-aurora/policies/AUR-WP-v{number}.md", "checksum");
}
