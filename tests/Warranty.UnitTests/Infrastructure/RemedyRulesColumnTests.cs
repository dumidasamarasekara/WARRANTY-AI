using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Infrastructure.Persistence;

namespace Warranty.UnitTests.Infrastructure;

/// <summary>
/// <c>policy.policy_versions.remedy_rules</c> stores exactly the <c>remedies</c> block of
/// contracts/remedy-rules.schema.json (camelCase, remedies spelled <c>"Repair"</c> etc., no derived
/// properties), and reads it back.
/// </summary>
public sealed class RemedyRulesColumnTests
{
    private static readonly RemedyRules Borealis = new(
        new RemedyChoice(RemedyType.Repair, null),
        new RepairNotEconomicalRule(60, null, [RemedyType.Refund, RemedyType.Replacement]),
        new RepairFailedRule(2, RemedyType.Refund, null),
        new ProtectionPlanRule(true, RemedyType.Repair, null));

    private const string BorealisJson = """
        {
          "coveredDefect":       { "remedy": "Repair" },
          "repairNotEconomical": { "limitPercentOfClaimValue": 60, "allowed": ["Refund", "Replacement"] },
          "repairFailed":        { "afterFailedAttempts": 2, "remedy": "Refund" },
          "protectionPlan":      { "liftsAccidentalDamageExclusion": true, "remedy": "Repair" }
        }
        """;

    private static IProperty Column
    {
        get
        {
            using var context = new WarrantyDbContext(
                new DbContextOptionsBuilder<WarrantyDbContext>().UseNpgsql("Host=localhost;Database=warranty;Username=unused").Options,
                new FakeTenantContext(null));
            return context.Model.FindEntityType(typeof(PolicyVersion))!.FindProperty(nameof(PolicyVersion.RemedyRules))!;
        }
    }

    [Fact]
    public void Remedy_rules_are_a_nullable_jsonb_column()
    {
        Column.GetColumnName().ShouldBe("remedy_rules");
        Column.GetColumnType().ShouldBe("jsonb");
        Column.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void Remedy_rules_are_stored_in_the_schema_shape()
    {
        var json = (string)Column.GetValueConverter()!.ConvertToProvider(Borealis)!;

        JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(BorealisJson)).ShouldBeTrue(json);
    }

    [Fact]
    public void Stored_remedy_rules_read_back_to_the_same_rules()
    {
        var converter = Column.GetValueConverter()!;
        var read = (RemedyRules)converter.ConvertFromProvider(BorealisJson)!;

        read.CoveredDefect!.Remedy.ShouldBe(RemedyType.Repair);
        read.RepairNotEconomical!.LimitPercentOfClaimValue.ShouldBe(60);
        read.RepairNotEconomical.Allowed.ShouldBe([RemedyType.Refund, RemedyType.Replacement]);
        read.RepairFailed!.AfterFailedAttempts.ShouldBe(2);
        read.RepairFailed.Remedy.ShouldBe(RemedyType.Refund);
        read.ProtectionPlan!.LiftsAccidentalDamageExclusion.ShouldBeTrue();
        read.ProtectionPlan.Remedy.ShouldBe(RemedyType.Repair);
        Should.NotThrow(read.Validate);
    }
}
