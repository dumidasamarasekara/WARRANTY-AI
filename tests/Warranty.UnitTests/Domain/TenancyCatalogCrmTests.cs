using Warranty.Domain.Catalog;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Tenancy;

namespace Warranty.UnitTests.Domain;

public sealed class TenancyCatalogCrmTests
{
    private static readonly Guid AuroraId = Guid.Parse("11111111-1111-7111-8111-111111111111");

    [Fact]
    public void Tenant_derives_namespace_and_container_from_slug()
    {
        var tenant = Tenant.Create(AuroraId, "aurora", " Aurora Electronics ", DateTimeOffset.UnixEpoch);

        tenant.KnowledgeNamespace.ShouldBe("tenant-aurora");
        tenant.BlobContainer.ShouldBe("tenant-aurora");
        tenant.DisplayName.ShouldBe("Aurora Electronics");
        tenant.Status.ShouldBe(TenantStatus.Active);
    }

    [Theory]
    [InlineData("Aurora")]
    [InlineData("ab")]
    [InlineData("1aurora")]
    [InlineData("aurora_x")]
    [InlineData("")]
    public void Tenant_rejects_invalid_slugs(string slug)
        => Should.Throw<ArgumentException>(() => Tenant.Create(AuroraId, slug, "Aurora", DateTimeOffset.UnixEpoch));

    [Theory]
    [InlineData("aurora.localhost", "aurora.localhost")]
    [InlineData("AURORA.localhost:5173", "aurora.localhost")]
    [InlineData(" borealis.localhost:443 ", "borealis.localhost")]
    public void Channel_hosts_are_lower_cased_and_port_stripped(string host, string expected)
        => TenantChannel.NormalizeHost(host).ShouldBe(expected);

    [Fact]
    public void Settings_treat_values_equal_to_the_limit_as_within_it()
    {
        var settings = TenantSettings.Create(AuroraId, "USD", 500m, 85);

        settings.IsAboveAutoApprovalLimit(500.00m).ShouldBeFalse();
        settings.IsAboveAutoApprovalLimit(500.01m).ShouldBeTrue();
        settings.IsBelowMinConfidence(85).ShouldBeFalse();
        settings.IsBelowMinConfidence(84).ShouldBeTrue();
        settings.RiskHighThreshold.ShouldBe(60);
        settings.AutoApproveEnabled.ShouldBeTrue();
        settings.AutoRejectEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Settings_match_always_review_categories_case_insensitively()
    {
        var settings = TenantSettings.Create(AuroraId, "USD", 1000m, 80, alwaysReviewCategories: [" Major-Appliance "]);

        settings.AlwaysRequiresReview("major-appliance").ShouldBeTrue();
        settings.AlwaysRequiresReview("MAJOR-APPLIANCE").ShouldBeTrue();
        settings.AlwaysRequiresReview("tablet").ShouldBeFalse();
        settings.AlwaysRequiresReview(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("usd", 500, 85, 60)]
    [InlineData("US", 500, 85, 60)]
    [InlineData("USD", 0, 85, 60)]
    [InlineData("USD", 500, 101, 60)]
    [InlineData("USD", 500, -1, 60)]
    [InlineData("USD", 500, 85, 0)]
    public void Settings_reject_invalid_values(string currency, int limit, int minConfidence, int highThreshold)
        => Should.Throw<ArgumentException>(
            () => TenantSettings.Create(AuroraId, currency, limit, minConfidence, riskHighThreshold: highThreshold));

    [Theory]
    [InlineData("US", Region.NA)]
    [InlineData("ca", Region.NA)]
    [InlineData("DE", Region.EU)]
    [InlineData(" fr ", Region.EU)]
    public void Region_is_derived_from_country(string country, Region expected)
    {
        RegionResolver.TryFromCountry(country, out var region).ShouldBeTrue();
        region.ShouldBe(expected);
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("JP")]
    [InlineData("")]
    [InlineData(null)]
    public void Countries_outside_NA_and_EU_have_no_region(string? country)
        => RegionResolver.TryFromCountry(country, out _).ShouldBeFalse();

    [Fact]
    public void Contacts_are_normalized_for_comparison()
    {
        ContactNormalizer.NormalizeEmail("  Jane.Doe@Example.COM ").ShouldBe("jane.doe@example.com");
        ContactNormalizer.NormalizePhone("+1 (555) 010-0199").ShouldBe("+15550100199");
        ContactNormalizer.NormalizePhone("0049 30.1234").ShouldBe("0049301234");
    }

    [Fact]
    public void Customer_normalizes_contacts_and_derives_region()
    {
        var customer = Customer.Create(Guid.CreateVersion7(), AuroraId, "Test Customer", "TEST@Example.com", "de", phone: "+49 30 1234");

        customer.Email.ShouldBe("test@example.com");
        customer.Phone.ShouldBe("+49301234");
        customer.Country.ShouldBe("DE");
        customer.Region.ShouldBe(Region.EU);
    }

    [Fact]
    public void Customer_outside_supported_regions_has_no_region()
        => Customer.Create(Guid.CreateVersion7(), AuroraId, "Test Customer", "t@example.com", "JP").Region.ShouldBeNull();

    [Fact]
    public void Product_normalizes_model_code_and_category()
    {
        var product = Product.Create(Guid.CreateVersion7(), AuroraId, " aur-tab10 ", "Aurora Tab 10", "Tablet", 450m, "usd");

        product.ModelCode.ShouldBe("AUR-TAB10");
        product.Category.ShouldBe("tablet");
        product.Currency.ShouldBe("USD");
        product.ClaimValue.ShouldBe(450.00m);
    }

    [Fact]
    public void Serial_numbers_are_stored_upper_case_and_trimmed()
        => ProductSerial.Create(AuroraId, "  at10-0001 ", Guid.CreateVersion7()).SerialNumber.ShouldBe("AT10-0001");
}
