using System.Text.Json.Nodes;
using Warranty.Evaluation.Metrics;
using static Warranty.UnitTests.Evaluation.EvaluationCases;

namespace Warranty.UnitTests.Evaluation;

public sealed class ExtractionAccuracyTests
{
    [Fact]
    public void Only_listed_fields_are_scored_and_strings_compare_trimmed_and_case_insensitively()
    {
        var expected = Obj("""{ "intake": { "problemCategory": "DISPLAY_DEFECT", "mentionsLiquid": false } }""");
        var observation = Observe() with
        {
            IntakeExtraction = Obj("""{ "problemCategory": " display_defect ", "mentionsLiquid": false, "summary": "not labelled" }"""),
        };

        var comparisons = ExtractionAccuracy.Compare("G-AUR-01", expected, observation);

        comparisons.Count.ShouldBe(2);
        comparisons.ShouldAllBe(c => c.Match);
    }

    [Fact]
    public void Numbers_match_to_the_cent_and_type_mismatches_are_misses()
    {
        var expected = Obj("""{ "invoice": { "totalAmount": 480.0, "legible": true, "currency": "USD" } }""");
        var observation = Observe() with { InvoiceExtraction = Obj("""{ "totalAmount": 480.004, "legible": "true", "currency": "EUR" }""") };

        var byField = ExtractionAccuracy.Compare("G-AUR-01", expected, observation).ToDictionary(c => c.Field);

        byField["totalAmount"].Match.ShouldBeTrue();
        byField["legible"].Match.ShouldBeFalse("a string is not a boolean");
        byField["currency"].Match.ShouldBeFalse();
    }

    [Fact]
    public void The_invoice_date_offset_is_compared_as_the_date_it_implies_from_the_claim_date()
    {
        var expected = Obj("""{ "invoice": { "invoiceDateOffsetMonths": -5 } }""");
        var right = Observe() with { InvoiceExtraction = Obj("""{ "invoiceDate": "2026-05-06" }""") };
        var wrong = Observe() with { InvoiceExtraction = Obj("""{ "invoiceDate": "2026-05-07" }""") };

        var hit = ExtractionAccuracy.Compare("G-AUR-01", expected, right).ShouldHaveSingleItem();
        hit.Field.ShouldBe("invoiceDate");
        hit.Expected.ShouldBe("\"2026-05-06\"");
        hit.Match.ShouldBeTrue();
        ExtractionAccuracy.Compare("G-AUR-01", expected, wrong).ShouldHaveSingleItem().Match.ShouldBeFalse();
    }

    [Fact]
    public void Damage_types_compare_as_sets_and_photos_by_upload_order()
    {
        var expected = Obj("""
            { "photos": [
                { "damageTypes": ["CRACKED_SCREEN", "DENTS_OR_IMPACT"] },
                { "visibleSerial": "AP6-24-0001" } ] }
            """);
        var observation = Observe() with
        {
            PhotoAnalyses = [Obj("""{ "damageTypes": ["DENTS_OR_IMPACT", "CRACKED_SCREEN"] }"""), Obj("""{ "visibleSerial": "NOT_VISIBLE" }""")],
        };

        var comparisons = ExtractionAccuracy.Compare("G-AUR-01", expected, observation);

        comparisons.Single(c => c.Section == "photo[0]").Match.ShouldBeTrue();
        comparisons.Single(c => c.Section == "photo[1]").Match.ShouldBeFalse();
        ExtractionAccuracy.ValuesMatch(JsonNode.Parse("""["NONE_VISIBLE"]"""), JsonNode.Parse("""["NONE_VISIBLE", "OTHER"]""")).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_output_or_field_counts_as_a_miss_and_accuracy_aggregates_by_section()
    {
        var expected = Obj("""
            { "intake": { "component": "SCREEN", "mentionsAccident": false },
              "invoice": { "legible": true },
              "photos": [ { "showsProduct": true }, { "showsProduct": true } ] }
            """);
        var observation = Observe() with
        {
            IntakeExtraction = Obj("""{ "component": "SCREEN" }"""),
            InvoiceExtraction = null,
            PhotoAnalyses = [Obj("""{ "showsProduct": true }""")],
        };

        var result = ExtractionAccuracy.Compute([Scored(Expect(extraction: expected), observation)]);

        result.Overall.ShouldBe(new Ratio(2, 5));
        result.BySection["intake"].ShouldBe(new Ratio(1, 2));
        result.BySection["invoice"].ShouldBe(new Ratio(0, 1));
        result.BySection["photo"].ShouldBe(new Ratio(1, 2));
        result.ByField["intake.mentionsAccident"].ShouldBe(new Ratio(0, 1));
        result.Mismatches.Select(m => m.Actual).ShouldAllBe(a => a == null);
    }

    [Fact]
    public void A_case_without_extraction_labels_scores_nothing()
        => ExtractionAccuracy.Compute([Scored(Expect(extraction: null), Observe())]).Overall.Value.ShouldBeNull();

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
}
