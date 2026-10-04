using System.Text.Json;
using System.Text.Json.Nodes;
using Warranty.AI.Harness.Schemas;

namespace Warranty.UnitTests.Harness;

/// <summary>
/// Structured agent outputs are validated against contracts/schemas plus the deterministic rules stated
/// in the schema descriptions (ranges, lengths, formats, required references). Whether a reference was
/// issued is the <c>ReferenceRegistry</c>'s job (see <see cref="ReferenceRegistryTests"/>).
/// </summary>
public sealed class SchemaValidatorTests
{
    private const string Pending = "Pending T054";

    private readonly SchemaValidator _validator = new();

    private static JsonObject Intake() => new()
    {
        ["problemCategory"] = "POWER_FAILURE",
        ["component"] = "BATTERY",
        ["symptoms"] = new JsonArray("does not turn on", "no charging light"),
        ["claimedCause"] = "SPONTANEOUS_FAILURE",
        ["mentionsAccident"] = false,
        ["mentionsLiquid"] = false,
        ["containsInstructionsToSystem"] = false,
        ["summary"] = "The tablet stopped turning on after normal use.",
    };

    private static JsonObject Invoice() => new()
    {
        ["evidenceRef"] = "EV-1",
        ["legible"] = true,
        ["sellerName"] = "Aurora Store",
        ["invoiceNumber"] = "INV-1001",
        ["invoiceDate"] = "2026-03-01",
        ["productDescription"] = "Aurora Tab 10",
        ["modelCodeOnInvoice"] = "AUR-TAB10",
        ["serialOnInvoice"] = "AT10-0001",
        ["totalAmount"] = 450.00m,
        ["currency"] = "USD",
        ["anomalies"] = new JsonArray(),
        ["containsInstructionsToSystem"] = false,
    };

    private static JsonObject Photo() => new()
    {
        ["evidenceRef"] = "EV-2",
        ["showsProduct"] = true,
        ["productTypeObserved"] = "tablet",
        ["visibleSerial"] = "NOT_VISIBLE",
        ["damageObserved"] = false,
        ["damageTypes"] = new JsonArray("NONE_VISIBLE"),
        ["consistentWithDescription"] = "CONSISTENT",
        ["imageQuality"] = "GOOD",
        ["containsInstructionsToSystem"] = false,
        ["confidence"] = 88,
        ["observations"] = "A tablet with an intact screen and no visible damage.",
    };

    private static JsonObject Policy() => new()
    {
        ["applicableClauses"] = new JsonArray(new JsonObject
        {
            ["ref"] = "POL-1",
            ["applies"] = true,
            ["effect"] = "GRANTS_COVERAGE",
            ["note"] = "Manufacturing defects are covered for 12 months in NA.",
        }),
        ["coverageAssessment"] = "COVERED",
        ["confidence"] = 90,
        ["relevantExclusions"] = new JsonArray(),
        ["ambiguity"] = new JsonObject { ["isAmbiguous"] = false, ["explanation"] = string.Empty },
        ["summary"] = "The claim falls within the standard coverage period.",
    };

    private static JsonObject Decision(string decision = "APPROVE") => new()
    {
        ["decision"] = decision,
        ["coverage"] = "COVERED",
        ["confidence"] = 92,
        ["risk"] = new JsonObject { ["level"] = "LOW", ["signals"] = new JsonArray() },
        ["evidenceRefs"] = new JsonArray(new JsonObject { ["ref"] = "EV-1", ["observation"] = "Invoice matches the claim." }),
        ["policyRefs"] = new JsonArray(new JsonObject { ["ref"] = "POL-1", ["relevance"] = "SUPPORTS_COVERAGE" }),
        ["missingInformation"] = new JsonArray(),
        ["reasoningSummary"] = "EV-1 confirms the purchase date; POL-1 covers manufacturing defects for 12 months.",
        ["claimantExplanation"] = "Your tablet is covered for manufacturing defects for 12 months.",
        ["manipulationDetected"] = false,
    };

    private static JsonObject Sample(string schemaId) => schemaId switch
    {
        SchemaValidator.IntakeExtraction => Intake(),
        SchemaValidator.InvoiceExtraction => Invoice(),
        SchemaValidator.PhotoAnalysis => Photo(),
        SchemaValidator.PolicyAssessment => Policy(),
        SchemaValidator.DecisionRecommendation => Decision(),
        _ => throw new ArgumentOutOfRangeException(nameof(schemaId), schemaId, "No sample."),
    };

    private SchemaValidationResult Validate(string schemaId, JsonNode output)
        => _validator.Validate(schemaId, JsonSerializer.SerializeToElement(output));

    private static void ShouldBeInvalidAt(SchemaValidationResult result, string property)
    {
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(property, StringComparison.Ordinal), $"no error names '{property}'");
    }

    [Fact]
    public void Five_output_schemas_are_known()
        => SchemaValidator.SchemaIds.ShouldBe(
            ["intake-extraction", "invoice-extraction", "photo-analysis", "policy-assessment", "decision-recommendation"],
            ignoreOrder: true);

    // ---- Embedded schemas -------------------------------------------------------------------------

    [Theory(Skip = Pending)]
    [InlineData(SchemaValidator.IntakeExtraction)]
    [InlineData(SchemaValidator.InvoiceExtraction)]
    [InlineData(SchemaValidator.PhotoAnalysis)]
    [InlineData(SchemaValidator.PolicyAssessment)]
    [InlineData(SchemaValidator.DecisionRecommendation)]
    public void Output_schema_is_the_contract_schema_for_its_id(string schemaId)
    {
        var schema = _validator.GetOutputSchema(schemaId);

        schema.Schema.GetProperty("$id").GetString().ShouldBe($"warranty-ai/{schemaId}/v1");
        schema.Schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact(Skip = Pending)]
    public void Output_schema_can_be_looked_up_by_its_full_id()
        => _validator.GetOutputSchema("warranty-ai/decision-recommendation/v1")
            .Schema.GetProperty("title").GetString().ShouldBe("DecisionRecommendation");

    [Theory(Skip = Pending)]
    [InlineData("claim-summary")]
    [InlineData("warranty-ai/decision-recommendation/v2")]
    [InlineData("")]
    public void Unknown_schema_id_is_rejected(string schemaId)
    {
        Should.Throw<ArgumentException>(() => _validator.GetOutputSchema(schemaId));
        Should.Throw<ArgumentException>(() => _validator.Validate(schemaId, JsonSerializer.SerializeToElement(Decision())));
    }

    // ---- Valid outputs -----------------------------------------------------------------------------

    [Theory(Skip = Pending)]
    [InlineData(SchemaValidator.IntakeExtraction)]
    [InlineData(SchemaValidator.InvoiceExtraction)]
    [InlineData(SchemaValidator.PhotoAnalysis)]
    [InlineData(SchemaValidator.PolicyAssessment)]
    [InlineData(SchemaValidator.DecisionRecommendation)]
    public void Conforming_output_is_valid(string schemaId)
    {
        var result = Validate(schemaId, Sample(schemaId));

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Errors.ShouldBeEmpty();
    }

    [Fact(Skip = Pending)]
    public void Valid_decision_with_risk_signals_and_missing_information_is_valid()
    {
        var output = Decision("REQUEST_MORE_INFORMATION");
        output["coverage"] = "UNDETERMINED";
        output["policyRefs"] = new JsonArray();
        output["risk"] = new JsonObject
        {
            ["level"] = "MEDIUM",
            ["signals"] = new JsonArray(new JsonObject
            {
                ["code"] = "SERIAL_MISMATCH_PHOTO",
                ["description"] = "Serial on the label differs from the claim.",
                ["evidenceRefs"] = new JsonArray("EV-2"),
            }),
        };
        output["missingInformation"] = new JsonArray(new JsonObject { ["item"] = "LEGIBLE_INVOICE", ["reason"] = "The invoice is blurred." });

        Validate(SchemaValidator.DecisionRecommendation, output).IsValid.ShouldBeTrue();
    }

    // ---- Schema violations -------------------------------------------------------------------------

    [Fact(Skip = Pending)]
    public void Missing_required_property_is_a_violation()
    {
        var output = Decision();
        output.Remove("coverage");

        ShouldBeInvalidAt(Validate(SchemaValidator.DecisionRecommendation, output), "coverage");
    }

    [Fact(Skip = Pending)]
    public void Additional_property_is_a_violation()
    {
        var output = Decision();
        output["tenantId"] = "tenant-borealis";

        ShouldBeInvalidAt(Validate(SchemaValidator.DecisionRecommendation, output), "tenantId");
    }

    [Theory(Skip = Pending)]
    [InlineData("decision", "AUTO_APPROVE")]
    [InlineData("coverage", "PARTIAL")]
    public void Value_outside_the_enum_is_a_violation(string property, string value)
    {
        var output = Decision();
        output[property] = value;

        ShouldBeInvalidAt(Validate(SchemaValidator.DecisionRecommendation, output), property);
    }

    [Fact(Skip = Pending)]
    public void Wrong_type_is_a_violation()
    {
        var output = Decision();
        output["confidence"] = "high";

        ShouldBeInvalidAt(Validate(SchemaValidator.DecisionRecommendation, output), "confidence");
    }

    [Fact(Skip = Pending)]
    public void Fractional_confidence_is_a_violation()
    {
        var output = Policy();
        output["confidence"] = 90.5;

        ShouldBeInvalidAt(Validate(SchemaValidator.PolicyAssessment, output), "confidence");
    }

    [Fact(Skip = Pending)]
    public void Violation_in_a_nested_object_is_reported()
    {
        var output = Decision();
        output["risk"]!["signals"] = new JsonArray(new JsonObject
        {
            ["code"] = "LOOKS_FISHY",
            ["description"] = "x",
            ["evidenceRefs"] = new JsonArray(),
        });

        ShouldBeInvalidAt(Validate(SchemaValidator.DecisionRecommendation, output), "code");
    }

    [Fact(Skip = Pending)]
    public void Unknown_photo_damage_type_is_a_violation()
    {
        var output = Photo();
        output["damageTypes"] = new JsonArray("WATER_DAMAGE");

        ShouldBeInvalidAt(Validate(SchemaValidator.PhotoAnalysis, output), "damageTypes");
    }

    [Theory(Skip = Pending)]
    [InlineData("[]")]
    [InlineData("\"APPROVE\"")]
    [InlineData("null")]
    public void Output_that_is_not_an_object_is_invalid_without_throwing(string json)
    {
        using var document = JsonDocument.Parse(json);

        _validator.Validate(SchemaValidator.DecisionRecommendation, document.RootElement).IsValid.ShouldBeFalse();
    }

    // ---- Confidence range --------------------------------------------------------------------------

    [Theory(Skip = Pending)]
    [InlineData(SchemaValidator.PhotoAnalysis, -1)]
    [InlineData(SchemaValidator.PhotoAnalysis, 101)]
    [InlineData(SchemaValidator.PolicyAssessment, -1)]
    [InlineData(SchemaValidator.PolicyAssessment, 101)]
    [InlineData(SchemaValidator.DecisionRecommendation, -5)]
    [InlineData(SchemaValidator.DecisionRecommendation, 101)]
    [InlineData(SchemaValidator.DecisionRecommendation, 1000)]
    public void Confidence_outside_0_to_100_is_invalid(string schemaId, int confidence)
    {
        var output = Sample(schemaId);
        output["confidence"] = confidence;

        ShouldBeInvalidAt(Validate(schemaId, output), "confidence");
    }

    [Theory(Skip = Pending)]
    [InlineData(SchemaValidator.PhotoAnalysis, 0)]
    [InlineData(SchemaValidator.PhotoAnalysis, 100)]
    [InlineData(SchemaValidator.PolicyAssessment, 0)]
    [InlineData(SchemaValidator.PolicyAssessment, 100)]
    [InlineData(SchemaValidator.DecisionRecommendation, 0)]
    [InlineData(SchemaValidator.DecisionRecommendation, 100)]
    public void Confidence_at_the_bounds_is_valid(string schemaId, int confidence)
    {
        var output = Sample(schemaId);
        output["confidence"] = confidence;

        Validate(schemaId, output).IsValid.ShouldBeTrue();
    }

    // ---- Text lengths ------------------------------------------------------------------------------

    [Theory(Skip = Pending)]
    [InlineData(SchemaValidator.IntakeExtraction, "summary", 400)]
    [InlineData(SchemaValidator.PhotoAnalysis, "observations", 600)]
    [InlineData(SchemaValidator.PolicyAssessment, "summary", 800)]
    [InlineData(SchemaValidator.DecisionRecommendation, "reasoningSummary", 1500)]
    [InlineData(SchemaValidator.DecisionRecommendation, "claimantExplanation", 800)]
    public void Text_over_its_maximum_length_is_invalid(string schemaId, string property, int maxLength)
    {
        var output = Sample(schemaId);
        output[property] = new string('a', maxLength + 1);

        ShouldBeInvalidAt(Validate(schemaId, output), property);
    }

    [Theory(Skip = Pending)]
    [InlineData(SchemaValidator.IntakeExtraction, "summary", 400)]
    [InlineData(SchemaValidator.PhotoAnalysis, "observations", 600)]
    [InlineData(SchemaValidator.PolicyAssessment, "summary", 800)]
    [InlineData(SchemaValidator.DecisionRecommendation, "reasoningSummary", 1500)]
    [InlineData(SchemaValidator.DecisionRecommendation, "claimantExplanation", 800)]
    public void Text_at_its_maximum_length_is_valid(string schemaId, string property, int maxLength)
    {
        var output = Sample(schemaId);
        output[property] = new string('a', maxLength);

        Validate(schemaId, output).IsValid.ShouldBeTrue();
    }

    [Theory(Skip = Pending)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void Intake_allows_at_most_eight_symptoms(int count, bool valid)
    {
        var output = Intake();
        output["symptoms"] = new JsonArray(Enumerable.Range(1, count).Select(i => (JsonNode?)JsonValue.Create($"symptom {i}")).ToArray());

        var result = Validate(SchemaValidator.IntakeExtraction, output);

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            ShouldBeInvalidAt(result, "symptoms");
        }
    }

    // ---- Formats -----------------------------------------------------------------------------------

    [Theory(Skip = Pending)]
    [InlineData("2026-03-01", true)]
    [InlineData("UNKNOWN", true)]
    [InlineData("2026-13-01", false)]
    [InlineData("2026-02-30", false)]
    [InlineData("2026-3-1", false)]
    [InlineData("01/03/2026", false)]
    [InlineData("2026-03-01T00:00:00Z", false)]
    [InlineData("", false)]
    public void Invoice_date_must_be_an_iso_date_or_unknown(string invoiceDate, bool valid)
    {
        var output = Invoice();
        output["invoiceDate"] = invoiceDate;

        var result = Validate(SchemaValidator.InvoiceExtraction, output);

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            ShouldBeInvalidAt(result, "invoiceDate");
        }
    }

    [Theory(Skip = Pending)]
    [InlineData("USD", true)]
    [InlineData("EUR", true)]
    [InlineData("UNKNOWN", true)]
    [InlineData("US Dollar", false)]
    [InlineData("$", false)]
    [InlineData("US", false)]
    public void Invoice_currency_must_be_an_iso_4217_code_or_unknown(string currency, bool valid)
    {
        var output = Invoice();
        output["currency"] = currency;

        var result = Validate(SchemaValidator.InvoiceExtraction, output);

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            ShouldBeInvalidAt(result, "currency");
        }
    }

    // ---- Required references -----------------------------------------------------------------------

    [Theory(Skip = Pending)]
    [InlineData("APPROVE")]
    [InlineData("REJECT")]
    [InlineData("REQUEST_MORE_INFORMATION")]
    [InlineData("HUMAN_REVIEW")]
    public void Decision_with_empty_evidence_refs_is_invalid(string decision)
    {
        var output = Decision(decision);
        output["evidenceRefs"] = new JsonArray();

        ShouldBeInvalidAt(Validate(SchemaValidator.DecisionRecommendation, output), "evidenceRefs");
    }

    [Theory(Skip = Pending)]
    [InlineData("APPROVE")]
    [InlineData("REJECT")]
    public void Approve_or_reject_without_policy_refs_is_invalid(string decision)
    {
        var output = Decision(decision);
        output["policyRefs"] = new JsonArray();

        ShouldBeInvalidAt(Validate(SchemaValidator.DecisionRecommendation, output), "policyRefs");
    }

    [Theory(Skip = Pending)]
    [InlineData("REQUEST_MORE_INFORMATION")]
    [InlineData("HUMAN_REVIEW")]
    public void Request_information_or_human_review_may_have_no_policy_refs(string decision)
    {
        var output = Decision(decision);
        output["policyRefs"] = new JsonArray();

        Validate(SchemaValidator.DecisionRecommendation, output).IsValid.ShouldBeTrue();
    }

    [Fact(Skip = Pending)]
    public void Reject_citing_a_policy_clause_is_valid()
    {
        var output = Decision("REJECT");
        output["coverage"] = "NOT_COVERED";
        output["policyRefs"] = new JsonArray(new JsonObject { ["ref"] = "POL-2", ["relevance"] = "DEFINES_PERIOD" });

        Validate(SchemaValidator.DecisionRecommendation, output).IsValid.ShouldBeTrue();
    }

    [Fact(Skip = Pending)]
    public void Whether_a_reference_was_issued_is_not_checked_by_the_schema_validator()
    {
        var output = Decision();
        output["policyRefs"] = new JsonArray(new JsonObject { ["ref"] = "POL-999", ["relevance"] = "SUPPORTS_COVERAGE" });

        Validate(SchemaValidator.DecisionRecommendation, output).IsValid.ShouldBeTrue();
    }

    [Fact(Skip = Pending)]
    public void Every_violation_is_reported()
    {
        var output = Decision("APPROVE");
        output["confidence"] = 150;
        output["evidenceRefs"] = new JsonArray();
        output["policyRefs"] = new JsonArray();
        output["claimantExplanation"] = new string('a', 801);

        var result = Validate(SchemaValidator.DecisionRecommendation, output);

        ShouldBeInvalidAt(result, "confidence");
        ShouldBeInvalidAt(result, "evidenceRefs");
        ShouldBeInvalidAt(result, "policyRefs");
        ShouldBeInvalidAt(result, "claimantExplanation");
    }
}
