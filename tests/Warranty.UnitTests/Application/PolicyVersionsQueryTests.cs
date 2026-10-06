using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Policies;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.UnitTests.Application;

/// <summary>The policy list read model behind <c>GET /api/policies</c> (T081): versions with effective dates and regions, own tenant only.</summary>
public sealed class PolicyVersionsQueryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("0199b000-0000-7000-8000-000000000002");

    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly List<PolicyVersion> _versions = [];
    private readonly TenantContextScope _tenant = TenantContextScope.Begin(TenantId, "aurora", "agent-sub");

    public PolicyVersionsQueryTests()
        => _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns(_ => _versions);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenant.Dispose();

    [Fact]
    public async Task Versions_are_listed_per_policy_code_in_version_order_with_dates_and_regions()
    {
        var warranty = Policy(TenantId, "AUR-WP", "Aurora Limited Warranty");
        var care = Policy(TenantId, "AUR-CARE", "Aurora Care");
        Version(warranty, 2, new DateOnly(2026, 7, 1), null, Region.EU, Region.NA);
        Version(care, 1, new DateOnly(2025, 3, 1), null, Region.EU);
        Version(warranty, 1, new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30), Region.NA);

        var list = await Query().ListAsync(Ct);

        list.Select(v => (v.PolicyCode, v.Version)).ShouldBe([("AUR-CARE", 1), ("AUR-WP", 1), ("AUR-WP", 2)]);
        list[1].ShouldBe(new PolicyVersionSummary("AUR-WP", "Aurora Limited Warranty", 1, new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30), list[1].Regions));
        list[1].Regions.ShouldBe([Region.NA]);
        list[2].EffectiveTo.ShouldBeNull();
        list[2].Regions.ShouldBe([Region.NA, Region.EU]);
    }

    [Fact]
    public async Task Versions_or_policies_of_another_tenant_are_never_returned()
    {
        var own = Policy(TenantId, "AUR-WP", "Aurora Limited Warranty");
        var foreign = Policy(OtherTenantId, "BOR-WP", "Borealis Care Warranty");
        Version(own, 1, new DateOnly(2025, 1, 1), null, Region.NA);
        Version(foreign, 1, new DateOnly(2024, 1, 1), null, Region.NA);

        var list = await Query().ListAsync(Ct);

        list.ShouldHaveSingleItem().PolicyCode.ShouldBe("AUR-WP");
    }

    [Fact]
    public async Task A_tenant_without_policies_gets_an_empty_list()
        => (await Query().ListAsync(Ct)).ShouldBeEmpty();

    [Fact]
    public async Task The_query_requires_a_resolved_tenant()
    {
        var unresolved = Substitute.For<ITenantContext>();
        unresolved.IsResolved.Returns(false);

        await Should.ThrowAsync<InvalidOperationException>(() => new PolicyVersionsQuery(unresolved, _policies).ListAsync(Ct));
    }

    private PolicyVersionsQuery Query() => new(_tenant, _policies);

    private WarrantyPolicy Policy(Guid tenantId, string code, string title)
    {
        var policy = WarrantyPolicy.Create(Guid.CreateVersion7(), tenantId, code, title);
        _policies.GetPolicyAsync(policy.Id, Arg.Any<CancellationToken>()).Returns(policy);
        return policy;
    }

    private void Version(WarrantyPolicy policy, int number, DateOnly from, DateOnly? to, params Region[] regions)
        => _versions.Add(PolicyVersion.Create(
            Guid.CreateVersion7(), policy.TenantId, policy.Id, number, from, to, regions, [],
            new CoverageTerms(regions.ToDictionary(r => r, _ => 12), new Dictionary<string, int>(), AccidentalDamageTerms.NotCovered, []),
            "p.md", "abc"));
}
