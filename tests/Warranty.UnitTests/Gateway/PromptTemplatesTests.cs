using Warranty.AI.Gateway.Prompts;
using Warranty.AI.Harness.Safety;
using Warranty.Application.Abstractions.AI;

namespace Warranty.UnitTests.Gateway;

/// <summary>The platform's v1 agent prompts (T055): front matter, shared safety rules and no tenant data.</summary>
public sealed class PromptTemplatesTests
{
    private static readonly PromptTemplateRegistry Registry = PromptTemplateRegistry.FromEmbeddedResources();

    private static readonly Dictionary<string, string> Variables = new()
    {
        [UntrustedContent.PreambleVariable] = UntrustedContent.Preamble,
    };

    public static TheoryData<string, string, string> Templates => new()
    {
        { "intake", "extraction", "warranty-ai/intake-extraction/v1" },
        { "evidence-invoice", "extraction", "warranty-ai/invoice-extraction/v1" },
        { "evidence-photo", "vision", "warranty-ai/photo-analysis/v1" },
        { "policy", "policy-reasoning", "warranty-ai/policy-assessment/v1" },
        { "decision", "adjudication", "warranty-ai/decision-recommendation/v1" },
    };

    public static TheoryData<string> TemplateIds => ["intake", "evidence-invoice", "evidence-photo", "policy", "decision"];

    private static PromptTemplate Get(string id) => Registry.Get(new PromptRef(id, 1));

    [Theory]
    [MemberData(nameof(Templates))]
    public void Each_template_names_its_contract_route_and_output_schema(string id, string route, string outputSchema)
    {
        var template = Get(id);

        template.Route.ShouldBe(route);
        template.OutputSchema.ShouldBe(outputSchema);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Each_template_renders_with_only_the_untrusted_content_rule(string id)
    {
        var rendered = Get(id).Render(Variables);

        rendered.ShouldContain(UntrustedContent.Preamble);
        rendered.ShouldContain("<untrusted_claim_content");
        rendered.ShouldNotContain("{{");
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Templates_hold_no_tenant_data(string id)
    {
        var body = Get(id).Body;

        foreach (var tenantMarker in new[] { "Aurora", "Borealis", "AUR-", "BOR-" })
        {
            body.ShouldNotContain(tenantMarker, Case.Insensitive);
        }
    }

    [Theory]
    [InlineData("evidence-invoice")]
    [InlineData("evidence-photo")]
    [InlineData("decision")]
    public void Evidence_reading_templates_use_only_issued_evidence_ids(string id)
        => Get(id).Body.ShouldContain("`EV-n`");

    [Theory]
    [InlineData("policy")]
    [InlineData("decision")]
    public void Policy_reading_templates_cite_only_issued_clauses_and_keep_global_knowledge_as_context(string id)
    {
        var body = Get(id).Body;

        body.ShouldContain("`POL-n`");
        body.ShouldContain("issued");
        body.ShouldContain("`GLB-n`");
        body.ShouldContain("never cite", Case.Insensitive);
    }

    [Theory]
    [InlineData("evidence-photo")]
    [InlineData("decision")]
    public void Unclear_photos_are_missing_information_not_risk(string id)
    {
        var body = Get(id).Body;

        body.ShouldContain("PHOTO_OF_DAMAGE");
        body.ShouldContain("PHOTO_OF_SERIAL_LABEL");
        body.ShouldContain("missing information");
    }

    [Fact]
    public void The_decision_template_keeps_the_claimant_explanation_free_of_risk_and_reference_ids()
    {
        var body = Get("decision").Body;

        body.ShouldContain("`claimantExplanation`");
        foreach (var term in new[] { "risk", "fraud", "manipulation", "duplicate", "`EV-`", "`POL-`", "`GLB-`", "signal code" })
        {
            body.ShouldContain(term);
        }
    }
}
