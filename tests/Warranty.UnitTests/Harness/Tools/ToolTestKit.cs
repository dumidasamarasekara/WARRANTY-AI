using System.Text.Json;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary>Shared data and checks for the read-only tool tests.</summary>
internal static class ToolTestKit
{
    public static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    public static readonly Guid RunId = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");
    public static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000c1");
    public static readonly Guid CustomerId = Guid.Parse("0199a000-0000-7000-8000-0000000000d1");
    public static readonly Guid ProductId = Guid.Parse("0199a000-0000-7000-8000-0000000000e1");

    public const string ModelCode = "AUR-TAB10";
    public const string Serial = "SN-TAB-0001";
    public const string Seller = "Brightline Electronics Inc.";
    public const string CustomerEmail = "philippa.quarrington@privacy-probe.test";

    public static readonly DateOnly PurchaseDate = new(2026, 1, 15);
    public static readonly DateTimeOffset SubmittedAt = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    public static Claim NewClaim(
        Guid? id = null,
        string serial = Serial,
        Region? region = Region.EU,
        bool inCatalog = true,
        DateTimeOffset? submittedAt = null,
        DateOnly? purchaseDate = null,
        string? contactEmail = CustomerEmail)
        => Claim.Submit(
            id ?? ClaimId, Aurora, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, CustomerId,
            contactEmail, contactEmail is null ? "+47 555 01 37" : null, ModelCode, inCatalog ? ProductId : null, serial, purchaseDate ?? PurchaseDate,
            Seller, 449.00m, region, "The tablet battery drains within an hour of a full charge.", submittedAt ?? SubmittedAt);

    public static Product NewProduct(string category = "tablet")
        => Product.Create(ProductId, Aurora, ModelCode, "Aurora Tab 10", category, 449m, "EUR");

    public static ToolInvocationContext Context(string caller, ReferenceRegistry? references = null)
        => new(new FakeTenantContext(Aurora), RunId, ClaimId, caller, references ?? new ReferenceRegistry());

    public static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static readonly JsonElement NoArguments = Args("{}");

    /// <summary>The strict-schema and no-tenant-input rules the registry enforces, plus the declared callers.</summary>
    public static void ShouldBeStrictReadOnlyTool(ITool tool, string name, params string[] callers)
    {
        var descriptor = tool.Descriptor;
        descriptor.Name.ShouldBe(name);
        descriptor.SideEffect.ShouldBe(ToolSideEffect.ReadOnly);
        descriptor.AllowedCallers.ShouldBe(callers, ignoreOrder: true);
        descriptor.Problems().ShouldBeEmpty();
        descriptor.InputSchema.GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False);
        AllPropertyNames(descriptor.InputSchema).ShouldAllBe(p => !p.Contains("tenant", StringComparison.OrdinalIgnoreCase));
        new ToolRegistry([tool]).For(callers[0]).ShouldHaveSingleItem();
    }

    /// <summary>Every property name declared anywhere in a schema, nested objects included.</summary>
    public static IEnumerable<string> AllPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in AllPropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>All string values of a JSON document, for "no personal data" assertions.</summary>
    public static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => [],
    };
}
