using System.Text.Json;
using NSubstitute;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Crm;
using static Warranty.UnitTests.Harness.Tools.ToolTestKit;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary><c>customer_lookup</c> and <c>product_lookup</c> (Intake; product also Evidence).</summary>
public sealed class CustomerAndProductLookupToolTests
{
    // Distinctive synthetic identifiers: any occurrence in a result is a leak (FR-006a).
    private const string CustomerName = "Philippa Quarrington-Vossberg";
    private const string CustomerPhone = "+47 415 55 01 37";
    private const string StreetAddress = "1789 Juniper Crescent Road";
    private const string City = "Sausalito";
    private const string PostalCode = "94965";

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICrmClient _crm = Substitute.For<ICrmClient>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly Customer _customer = Customer.Create(
        CustomerId, Aurora, CustomerName, CustomerEmail, "DE", CustomerPhone, StreetAddress, City, PostalCode);

    public CustomerAndProductLookupToolTests()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(NewClaim());
        _crm.GetCustomerAsync(CustomerId, Arg.Any<CancellationToken>()).Returns(_customer);
        _claims.CountCustomerClaimsAsync(CustomerId, SubmittedAt, Arg.Any<CancellationToken>()).Returns(2);
    }

    [Fact]
    public void Customer_lookup_takes_no_arguments_and_is_for_intake_only()
    {
        var tool = new CustomerLookupTool(_claims, _crm);

        ShouldBeStrictReadOnlyTool(tool, "customer_lookup", AgentNames.Intake);
        tool.Descriptor.InputSchema.GetProperty("properties").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_known_customer_is_verified_with_region_and_prior_claims_and_no_personal_data()
    {
        var result = await new CustomerLookupTool(_claims, _crm).InvokeAsync(NoArguments, Context(AgentNames.Intake), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Content.GetProperty("verified").GetBoolean().ShouldBeTrue();
        result.Content.GetProperty("region").GetString().ShouldBe("EU");
        result.Content.GetProperty("priorClaimsCount").GetInt32().ShouldBe(2);
        result.Content.EnumerateObject().Select(p => p.Name).ShouldBe(["verified", "region", "priorClaimsCount"]);

        var texts = Strings(result.Content).Append(result.Summary).ToList();
        foreach (var identifier in new[] { CustomerName, "Quarrington", CustomerEmail, CustomerPhone, StreetAddress, City, PostalCode })
        {
            texts.ShouldAllBe(t => !t.Contains(identifier, StringComparison.OrdinalIgnoreCase), identifier);
        }
    }

    [Fact]
    public async Task An_unknown_customer_is_not_verified()
    {
        _crm.GetCustomerAsync(CustomerId, Arg.Any<CancellationToken>()).Returns((Customer?)null);

        var result = await new CustomerLookupTool(_claims, _crm).LookupAsync(ClaimId, TestContext.Current.CancellationToken);

        result.ShouldBe(new CustomerLookupResult(false, null, 0));
    }

    [Fact]
    public async Task A_claim_whose_contact_email_is_not_the_customers_is_not_verified()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(NewClaim(contactEmail: "someone.else@privacy-probe.test"));

        var result = await new CustomerLookupTool(_claims, _crm).LookupAsync(ClaimId, TestContext.Current.CancellationToken);

        result.Verified.ShouldBeFalse();
    }

    [Fact]
    public async Task A_customer_outside_NA_and_EU_has_no_region()
    {
        _crm.GetCustomerAsync(CustomerId, Arg.Any<CancellationToken>())
            .Returns(Customer.Create(CustomerId, Aurora, CustomerName, CustomerEmail, "JP"));

        var result = await new CustomerLookupTool(_claims, _crm).LookupAsync(ClaimId, TestContext.Current.CancellationToken);

        (result.Verified, result.Region).ShouldBe((true, (string?)null));
    }

    [Fact]
    public void Product_lookup_takes_model_and_serial_and_serves_intake_and_evidence()
    {
        var tool = new ProductLookupTool(_catalog);

        ShouldBeStrictReadOnlyTool(tool, "product_lookup", AgentNames.Intake, AgentNames.Evidence);
        tool.Descriptor.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["modelCode", "serialNumber"]);
    }

    [Fact]
    public async Task A_catalog_product_with_its_serial_is_found_and_registered()
    {
        var product = NewProduct();
        _catalog.FindProductByModelAsync(ModelCode, Arg.Any<CancellationToken>()).Returns(product);
        _catalog.FindSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(Aurora, Serial, product.Id));

        var result = await new ProductLookupTool(_catalog).InvokeAsync(
            Args($$"""{"modelCode":"{{ModelCode}}","serialNumber":"{{Serial}}"}"""), Context(AgentNames.Evidence), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        JsonSerializer.Deserialize<ProductLookupResult>(result.Content, JsonSerializerOptions.Web)
            .ShouldBe(new ProductLookupResult(true, true, "Aurora Tab 10", "tablet"));
    }

    [Fact]
    public async Task A_serial_of_another_model_is_not_registered()
    {
        _catalog.FindProductByModelAsync(ModelCode, Arg.Any<CancellationToken>()).Returns(NewProduct());
        _catalog.FindSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(Aurora, Serial, Guid.NewGuid()));

        var result = await new ProductLookupTool(_catalog).LookupAsync(ModelCode, Serial, TestContext.Current.CancellationToken);

        result.ShouldBe(new ProductLookupResult(true, false, "Aurora Tab 10", "tablet"));
    }

    [Fact]
    public async Task An_unknown_model_is_not_found()
    {
        var result = await new ProductLookupTool(_catalog).LookupAsync("NOPE-1", Serial, TestContext.Current.CancellationToken);

        result.ShouldBe(new ProductLookupResult(false, false, null, null));
        await _catalog.DidNotReceiveWithAnyArgs().FindSerialAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("""{"modelCode":"   ","serialNumber":"SN-1"}""")]
    [InlineData("""{"modelCode":"AUR-TAB10","serialNumber":"SN-1234567890-1234567890-1234567890-1234567890-1234567890-1234567890"}""")]
    public async Task Blank_or_overlong_identifiers_are_an_error(string arguments)
    {
        var result = await new ProductLookupTool(_catalog).InvokeAsync(Args(arguments), Context(AgentNames.Intake), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        await _catalog.DidNotReceiveWithAnyArgs().FindProductByModelAsync(default!, TestContext.Current.CancellationToken);
    }
}
