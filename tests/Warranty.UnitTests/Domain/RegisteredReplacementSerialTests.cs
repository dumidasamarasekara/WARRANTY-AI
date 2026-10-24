using Warranty.Domain.Catalog;
using Warranty.Domain.Common;
using Warranty.Domain.Integration;

namespace Warranty.UnitTests.Domain;

/// <summary><see cref="RegisteredReplacementSerial"/> (003 data-model.md <c>catalog.replacement_serials</c>) and <see cref="ServiceCenter.IssuesReplacements"/>.</summary>
public sealed class RegisteredReplacementSerialTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid ClaimId = Guid.CreateVersion7();
    private static readonly DateOnly Purchase = new(2025, 3, 1);
    private static readonly DateOnly HandedOver = new(2026, 10, 20);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_normalises_both_serials_and_keeps_the_facts()
    {
        var registered = RegisteredReplacementSerial.Register(
            Tenant, "  rep-0001 ", ProductId, " aur-tab10-0042", ClaimId, Purchase, HandedOver, new DateOnly(2027, 4, 20), Now);

        registered.Id.Version.ShouldBe(7);
        (registered.TenantId, registered.SerialNumber, registered.ProductId, registered.OriginalSerial, registered.ClaimId)
            .ShouldBe((Tenant, "REP-0001", ProductId, "AUR-TAB10-0042", ClaimId));
        (registered.OriginalPurchaseDate, registered.HandedOverOn, registered.CoverEndDate, registered.CreatedAt)
            .ShouldBe((Purchase, HandedOver, new DateOnly(2027, 4, 20), Now));
    }

    [Fact]
    public void Register_refuses_missing_ids_the_claims_own_serial_and_a_hand_over_before_the_purchase()
    {
        Should.Throw<ArgumentException>(() => Register(tenant: Guid.Empty));
        Should.Throw<ArgumentException>(() => Register(product: Guid.Empty));
        Should.Throw<ArgumentException>(() => Register(claim: Guid.Empty));
        Should.Throw<ArgumentException>(() => Register(serial: " "));
        Should.Throw<ArgumentException>(() => Register(serial: "aur-tab10-0042 "));
        Should.Throw<ArgumentException>(() => Register(handedOver: Purchase.AddDays(-1)));
    }

    [Theory]
    [InlineData("2027-03-01", 0, "2027-03-01")]
    [InlineData("2027-03-01", 6, "2027-04-20")]
    [InlineData("2027-05-01", 6, "2027-05-01")]
    [InlineData("2026-09-30", 0, "2026-10-20")]
    public void The_cover_end_is_the_later_of_the_original_end_and_the_hand_over_plus_the_minimum(
        string originalEnd, int months, string expected)
        => RegisteredReplacementSerial.CoverEndOf(DateOnly.Parse(originalEnd), HandedOver, months).ShouldBe(DateOnly.Parse(expected));

    [Fact]
    public void A_service_centre_issues_replacements_only_when_set()
    {
        ServiceCenter.Create(Guid.CreateVersion7(), Tenant, Region.EU, "Centre", ["tablet"]).IssuesReplacements.ShouldBeFalse();
        ServiceCenter.Create(Guid.CreateVersion7(), Tenant, Region.EU, "Centre", ["tablet"], issuesReplacements: true)
            .IssuesReplacements.ShouldBeTrue();
    }

    private static RegisteredReplacementSerial Register(
        Guid? tenant = null, Guid? product = null, Guid? claim = null, string serial = "REP-0001", DateOnly? handedOver = null)
        => RegisteredReplacementSerial.Register(
            tenant ?? Tenant, serial, product ?? ProductId, "AUR-TAB10-0042", claim ?? ClaimId, Purchase, handedOver ?? HandedOver,
            new DateOnly(2027, 4, 20), Now);
}
