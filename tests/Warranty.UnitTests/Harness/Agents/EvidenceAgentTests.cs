using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Warranty.AI.Gateway;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.Providers.Anthropic;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Harness;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Guardrails.Rules;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Harness.Agents;

/// <summary>
/// The Evidence Agent (T062): invoice extraction on <c>extraction</c>, one parallel <c>vision</c> call per photo
/// with the original bytes, <c>invoice_validation</c> and the serial-in-photo rows, legibility and missing items,
/// status mapping, persistence, and replay of the S1 evidence fixtures — through the real gateway.
/// </summary>
public sealed class EvidenceAgentTests : IAsyncDisposable
{
    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid RunId = Guid.Parse("0199a000-0000-7000-8000-0000000000a7");
    private static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000c7");
    private static readonly Guid InvoiceId = Guid.Parse("0199a000-0000-7000-8000-0000000000e1");
    private static readonly Guid Photo1Id = Guid.Parse("0199a000-0000-7000-8000-0000000000e2");
    private static readonly Guid Photo2Id = Guid.Parse("0199a000-0000-7000-8000-0000000000e3");
    private static readonly DateOnly Today = new(2026, 10, 4);
    private static readonly DateOnly PurchaseDate = new(2026, 6, 4);

    private const string ModelCode = "AUR-TAB10";
    private const string Serial = "AT10-24-0001";
    private const string Description = "My Aurora Tab 10 suddenly stopped turning on. The screen stays black and there is no charging light.";
    private const string Summary = "The tablet stopped powering on during normal use.";

    private const string Extraction = $$"""
        {"problemCategory":"POWER_FAILURE","component":"MAINBOARD","symptoms":["does not power on"],
         "claimedCause":"SPONTANEOUS_FAILURE","mentionsAccident":false,"mentionsLiquid":false,
         "containsInstructionsToSystem":false,"summary":"{{Summary}}"}
        """;

    private static readonly byte[] InvoiceBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");
    private static readonly byte[] Photo1Bytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03, 0x04, 0xFF, 0xD9];
    private static readonly byte[] Photo2Bytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x05, 0x06, 0x07, 0x08, 0x09, 0xFF, 0xD9];

    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly FakeEvidenceTools _tools = new();
    private readonly ScriptedModelProvider _model = new(AnthropicModelProvider.ProviderName);
    private readonly ReferenceRegistry _references = new();
    private ServiceProvider? _services;

    public EvidenceAgentTests()
    {
        _documents.OpenEvidenceAsync(Blob(InvoiceId), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(InvoiceBytes));
        _documents.OpenEvidenceAsync(Blob(Photo1Id), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Photo1Bytes));
        _documents.OpenEvidenceAsync(Blob(Photo2Id), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Photo2Bytes));
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    // ---- Descriptors and registration ---------------------------------------------------------------

    [Fact]
    public void The_descriptors_name_the_evidence_caller_and_one_route_prompt_and_schema_per_file_kind()
    {
        var schemas = new SchemaValidator();

        EvidenceAgent.Agent.Name.ShouldBe(AgentNames.Evidence);
        EvidenceAgent.Agent.AllowedTools.ShouldBe([ToolNames.InvoiceValidation, ToolNames.ProductLookup]);

        var invoice = EvidenceAgent.InvoiceStep;
        invoice.Name.ShouldBe("evidence-invoice");
        invoice.Route.ShouldBe("extraction");
        invoice.Prompt.ShouldBe(new PromptRef("evidence-invoice", 1));
        invoice.OutputSchemaId.ShouldBe(schemas.GetOutputSchema(SchemaValidator.InvoiceExtraction).SchemaId);

        var photo = EvidenceAgent.PhotoStep;
        photo.Name.ShouldBe("evidence-photo");
        photo.Route.ShouldBe("vision");
        photo.Prompt.ShouldBe(new PromptRef("evidence-photo", 1));
        photo.OutputSchemaId.ShouldBe(schemas.GetOutputSchema(SchemaValidator.PhotoAnalysis).SchemaId);
        photo.AllowedTools.ShouldBe([ToolNames.ProductLookup]);

        foreach (var descriptor in new[] { EvidenceAgent.Agent, invoice, photo })
        {
            descriptor.MaxTurns.ShouldBe(3);
            descriptor.InputTokenBudget.ShouldBe(4_000);
        }
    }

    [Fact]
    public async Task The_harness_registers_the_evidence_agent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Aurora));
        foreach (var port in new[]
                 {
                     typeof(IClaimRepository), typeof(ICatalogRepository), typeof(IPolicyRepository), typeof(ICrmClient), typeof(IKnowledgeRetriever),
                     typeof(IAiOpsRepository), typeof(ISecurityEventWriter), typeof(IPiiRedactor), typeof(IAdjudicationRepository), typeof(IDocumentStore),
                     typeof(ITenantRepository),
                 })
        {
            services.AddScoped(port, _ => Substitute.For([port], []));
        }

        services.AddWarrantyAiHarness();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IAgent<EvidenceInput, EvidenceResult>>()
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<EvidenceAgent>());
    }

    // ---- A clear claim ------------------------------------------------------------------------------

    [Fact]
    public async Task A_clear_claim_yields_one_finding_per_file_with_consistency_rows_and_photo_confidence()
    {
        Script();

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var evidence = result.Output.ShouldNotBeNull();
        evidence.Findings.Select(f => (f.EvidenceId, f.Kind)).ShouldBe(
        [
            (InvoiceId, EvidenceFindingKind.InvoiceExtraction), (Photo1Id, EvidenceFindingKind.PhotoAnalysis), (Photo2Id, EvidenceFindingKind.PhotoAnalysis),
        ]);
        evidence.Findings.ShouldAllBe(f => f.RunId == RunId && f.TenantId == Aurora);

        var invoice = evidence.Findings[0];
        invoice.Confidence.ShouldBeNull();
        invoice.Consistency.Select(c => c.Field).ShouldBe(["serialNumber", "modelCode", "purchaseDate", "purchasePrice", "seller"]);
        invoice.Consistency.ShouldAllBe(c => c.Match);
        InvoiceExtractionOutput.From(invoice).ShouldNotBeNull().SerialOnInvoice.ShouldBe(Serial);

        evidence.Findings[1].Confidence.ShouldBe(90);
        evidence.Findings[1].Consistency.ShouldBeEmpty("the serial is not visible in the first photo");
        evidence.Findings[2].Confidence.ShouldBe(92);
        evidence.Findings[2].Consistency.ShouldHaveSingleItem().ShouldBe(new ConsistencyCheck("serialNumber", Serial, Serial, true));

        evidence.ConsistencyChecks.ShouldBe(evidence.Findings.SelectMany(f => f.Consistency).ToList());
        evidence.MissingItems.ShouldBeEmpty();
        evidence.Validation.ShouldHaveSingleItem().ShouldBe(new ValidationCheck("INVOICE_LEGIBLE", true, "Legible: EV-1."));
        foreach (var finding in evidence.Findings)
        {
            _adjudication.Received(1).AddEvidenceFinding(finding);
        }

        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Evidence_references_are_issued_invoice_first_then_photos_in_upload_order()
    {
        Script();

        await RunAsync(Case());

        _references.Entries.Select(e => (e.Id, e.TargetId)).ShouldBe([("EV-1", InvoiceId), ("EV-2", Photo1Id), ("EV-3", Photo2Id)]);
    }

    [Fact]
    public async Task References_already_issued_by_the_run_are_kept()
    {
        _references.IssueEvidence(Photo2Id);
        Script();

        var result = await RunAsync(Case());

        _references.Entries.Select(e => (e.Id, e.TargetId)).ShouldBe([("EV-1", Photo2Id), ("EV-2", InvoiceId), ("EV-3", Photo1Id)]);
        result.Status.ShouldBe(AgentStatus.Succeeded);
        AttachmentRefs("vision").ShouldBe(["EV-3", "EV-1"], ignoreOrder: true);
    }

    // ---- The model requests -------------------------------------------------------------------------

    [Fact]
    public async Task The_invoice_is_read_on_the_extraction_route_as_the_original_pdf_document()
    {
        Script();

        await RunAsync(Case());

        var request = _model.Requests.Single(r => r.Request.Route == "extraction");
        request.Request.Context.Agent.ShouldBe("evidence-invoice");
        request.Request.Context.TenantId.ShouldBe(Aurora);
        request.Request.Context.ClaimId.ShouldBe(ClaimId);
        request.Request.Context.RunId.ShouldBe(RunId);
        request.Request.Prompt.ShouldBe(new PromptRef("evidence-invoice", 1));
        request.Request.OutputSchema!.SchemaId.ShouldBe("warranty-ai/invoice-extraction/v1");
        request.Request.PromptVariables.ShouldHaveSingleItem().Key.ShouldBe(UntrustedContent.PreambleVariable);
        request.Request.Tools.Select(t => t.Name).ShouldBe([ToolNames.InvoiceValidation, ToolNames.ProductLookup], ignoreOrder: true);

        var parts = request.Messages.ShouldHaveSingleItem().Parts;
        var document = parts.OfType<DocumentPart>().ShouldHaveSingleItem();
        document.EvidenceRef.ShouldBe("EV-1");
        document.PdfData.ToArray().ShouldBe(InvoiceBytes);
        parts.OfType<ImagePart>().ShouldBeEmpty();
        var text = Texts(parts);
        text.ShouldContain("EV-1");
        text.ShouldContain(ModelCode);
        text.ShouldContain("Aurora Tab 10");
        text.ShouldNotContain(Serial, customMessage: "the model reads the serial without being told the claimed one");
        text.ShouldNotContain(Description);
    }

    [Fact]
    public async Task An_image_invoice_is_sent_as_an_image_part_on_the_extraction_route()
    {
        var pngInvoice = Evidence(InvoiceId, EvidenceKind.Invoice, "image/png");
        Script();

        await RunAsync(Case(evidence: [pngInvoice, Evidence(Photo1Id, EvidenceKind.Photo), Evidence(Photo2Id, EvidenceKind.Photo)]));

        var parts = _model.Requests.Single(r => r.Request.Route == "extraction").Messages.Single().Parts;
        var image = parts.OfType<ImagePart>().ShouldHaveSingleItem();
        image.EvidenceRef.ShouldBe("EV-1");
        image.MediaType.ShouldBe("image/png");
        image.Data.ToArray().ShouldBe(InvoiceBytes);
        parts.OfType<DocumentPart>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_photo_is_analysed_on_the_vision_route_with_its_original_bytes_and_a_neutral_problem_summary()
    {
        Script();

        await RunAsync(Case());

        var photos = _model.Requests.Where(r => r.Request.Route == "vision").ToList();
        photos.Count.ShouldBe(2);
        foreach (var request in photos)
        {
            request.Request.Context.Agent.ShouldBe("evidence-photo");
            request.Request.Prompt.ShouldBe(new PromptRef("evidence-photo", 1));
            request.Request.OutputSchema!.SchemaId.ShouldBe("warranty-ai/photo-analysis/v1");
            request.Request.Tools.Select(t => t.Name).ShouldBe([ToolNames.ProductLookup]);

            var parts = request.Messages.ShouldHaveSingleItem().Parts;
            var image = parts.OfType<ImagePart>().ShouldHaveSingleItem();
            image.MediaType.ShouldBe("image/jpeg");
            image.Data.ToArray().ShouldBe(image.EvidenceRef == "EV-2" ? Photo1Bytes : Photo2Bytes, "the original bytes, never resized by the agent");

            var summary = parts.OfType<UntrustedTextPart>().ShouldHaveSingleItem();
            summary.Label.ShouldBe(EvidenceAgent.ProblemSummaryLabel);
            summary.Text.ShouldBe(Summary);
            var text = Texts(parts);
            text.ShouldContain(image.EvidenceRef);
            text.ShouldContain("POWER_FAILURE");
            text.ShouldContain("MAINBOARD");
            text.ShouldContain("Aurora Tab 10");
            text.ShouldNotContain(Summary, customMessage: "the summary travels only as untrusted content");
            text.ShouldNotContain(Description);
            text.ShouldNotContain(Serial);
        }
    }

    [Fact]
    public async Task Photo_calls_run_in_parallel_start_in_upload_order_and_keep_their_own_references()
    {
        var probe = new ParallelProbe(photos: 2);

        var result = await RunAsync(Case(), probe, AiGatewayOptions.LiveMode);

        result.Status.ShouldBe(AgentStatus.Succeeded, string.Join("; ", result.Diagnostics));
        probe.PhotoArrivals.ShouldBe(["EV-2", "EV-3"]);
        probe.PhotoCompletions.ShouldBe(["EV-3", "EV-2"], "the first photo answers last");
        var photos = result.Output!.Findings.Where(f => f.Kind == EvidenceFindingKind.PhotoAnalysis).ToList();
        photos.Select(f => f.EvidenceId).ShouldBe([Photo1Id, Photo2Id]);
        PhotoAnalysisOutput.From(photos[0])!.EvidenceRef.ShouldBe("EV-2");
        PhotoAnalysisOutput.From(photos[0])!.VisibleSerial.ShouldBe("NOT_VISIBLE");
        PhotoAnalysisOutput.From(photos[1])!.VisibleSerial.ShouldBe(Serial);
    }

    [Fact]
    public async Task A_result_naming_another_reference_is_stored_with_the_issued_one()
    {
        Script(photo: reference => PhotoJson(reference == "EV-2" ? "EV-3" : "EV-2", serial: reference == "EV-3" ? Serial : "NOT_VISIBLE"));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var photos = result.Output!.Findings.Where(f => f.Kind == EvidenceFindingKind.PhotoAnalysis).ToList();
        PhotoAnalysisOutput.From(photos[0])!.EvidenceRef.ShouldBe("EV-2");
        PhotoAnalysisOutput.From(photos[1])!.EvidenceRef.ShouldBe("EV-3");
        result.Diagnostics.Count(d => d.Contains("the model named the file", StringComparison.Ordinal)).ShouldBe(2);
    }

    // ---- Invoice validation -------------------------------------------------------------------------

    [Fact]
    public async Task Invoice_validation_runs_through_the_scoped_invoker_with_the_extracted_values()
    {
        Script();

        await RunAsync(Case());

        var call = _tools.Calls.ShouldHaveSingleItem();
        call.ToolName.ShouldBe(ToolNames.InvoiceValidation);
        call.Arguments.GetProperty("invoiceRef").GetString().ShouldBe("EV-1");
        var extracted = call.Arguments.GetProperty("extracted");
        extracted.GetProperty("invoiceDate").GetString().ShouldBe("2026-06-04");
        extracted.GetProperty("modelCode").GetString().ShouldBe(ModelCode);
        extracted.GetProperty("serial").GetString().ShouldBe(Serial);
        extracted.GetProperty("amount").GetDecimal().ShouldBe(450m);
        extracted.GetProperty("seller").GetString().ShouldBe("Aurora Store");
    }

    [Fact]
    public async Task Fields_the_invoice_does_not_show_have_no_row_and_a_differing_seller_is_a_mismatch()
    {
        Script(invoice: reference => InvoiceJson(reference, serial: "UNKNOWN", amount: 0m, seller: "Borealis Outlet"));

        var result = await RunAsync(Case());

        var rows = result.Output!.Findings[0].Consistency;
        rows.Select(c => c.Field).ShouldBe(["modelCode", "purchaseDate", "seller"]);
        rows.Single(c => c.Field == "seller").ShouldBe(new ConsistencyCheck("seller", "Aurora Store", "Borealis Outlet", false));
    }

    [Fact]
    public async Task When_invoice_validation_fails_the_same_rules_run_locally_with_a_diagnostic()
    {
        _tools.FailInvoiceValidation = true;
        Script(invoice: reference => InvoiceJson(reference, serial: "AT10-24-9999"));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        result.Diagnostics.ShouldContain(d => d.StartsWith("invoice_validation EV-1:", StringComparison.Ordinal) && d.Contains("compared locally", StringComparison.Ordinal));
        result.Output!.Findings[0].Consistency.Single(c => c.Field == "serialNumber").Match.ShouldBeFalse();
    }

    [Fact]
    public void Invoice_validation_rows_drop_fields_that_were_not_compared()
    {
        var rows = EvidenceAgent.ToConsistency(
        [
            new InvoiceFieldCheck("serialNumber", Serial, null, EvidenceMatch.NotCompared),
            new InvoiceFieldCheck("modelCode", ModelCode, ModelCode, EvidenceMatch.Match),
            new InvoiceFieldCheck("seller", "Aurora Store", "Other", EvidenceMatch.Mismatch),
        ]);

        rows.ShouldBe([new ConsistencyCheck("modelCode", ModelCode, ModelCode, true), new ConsistencyCheck("seller", "Aurora Store", "Other", false)]);
    }

    // ---- Legibility ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_illegible_invoice_fails_INVOICE_LEGIBLE_asks_for_a_legible_invoice_and_is_not_compared()
    {
        Script(invoice: reference => InvoiceJson(reference, legible: false, serial: "UNKNOWN", amount: 0m, seller: "UNKNOWN"));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var evidence = result.Output!;
        evidence.Validation.ShouldHaveSingleItem().ShouldBe(new ValidationCheck("INVOICE_LEGIBLE", false, "Not legible: EV-1."));
        evidence.MissingItems.ShouldHaveSingleItem().Item.ShouldBe("LEGIBLE_INVOICE");
        evidence.Findings[0].Consistency.ShouldBeEmpty();
        _tools.Calls.ShouldBeEmpty();
    }

    // ---- Serial in photo ----------------------------------------------------------------------------

    [Theory]
    [InlineData("NOT_VISIBLE", null)]
    [InlineData("UNKNOWN", null)]
    [InlineData("", null)]
    [InlineData("at10 24 0001", true)]
    [InlineData("AT10-24-0002", false)]
    public void The_serial_in_a_photo_is_compared_with_the_claimed_serial_only_when_it_is_legible(string visible, bool? match)
    {
        var rows = EvidenceAgent.SerialCheck(Serial, Photo(visibleSerial: visible));

        if (match is null)
        {
            rows.ShouldBeEmpty();
        }
        else
        {
            rows.ShouldHaveSingleItem().ShouldBe(new ConsistencyCheck("serialNumber", Serial, visible, match.Value));
        }
    }

    [Fact]
    public async Task A_different_serial_in_a_photo_becomes_a_photo_serial_check_for_the_risk_capability()
    {
        Script(photo: reference => PhotoJson(reference, serial: reference == "EV-3" ? "AT10-24-0777" : "NOT_VISIBLE"));

        var result = await RunAsync(Case());

        var facts = EvidenceRiskFacts.From(result.Output, _references);
        facts.InvoiceChecks.ShouldAllBe(c => c.EvidenceRef == "EV-1");
        facts.InvoiceChecks.Count.ShouldBe(5);
        var photo = facts.PhotoSerialChecks.ShouldHaveSingleItem();
        photo.EvidenceRef.ShouldBe("EV-3");
        photo.Check.ShouldBe(new ConsistencyCheck("serialNumber", Serial, "AT10-24-0777", false));
        RiskAssessor.EvidenceSignals(Case(), facts, Today).ShouldHaveSingleItem().Code.ShouldBe(RiskSignalCode.SerialMismatchPhoto);
    }

    // ---- Missing items ------------------------------------------------------------------------------

    [Fact]
    public void Photos_that_show_neither_the_product_nor_damage_ask_for_a_photo_of_the_damage_and_of_the_serial_label()
    {
        var missing = EvidenceAgent.MissingItems(
            [],
            [Photo(showsProduct: false, damageObserved: false, consistency: "CANNOT_DETERMINE"), Photo(quality: "UNUSABLE")]);

        missing.Select(m => m.Item).ShouldBe(["PHOTO_OF_DAMAGE", "PHOTO_OF_SERIAL_LABEL"]);
    }

    [Fact]
    public void A_damage_close_up_without_the_product_asks_only_for_a_photo_of_the_serial_label()
    {
        var missing = EvidenceAgent.MissingItems([], [Photo(showsProduct: false, damageObserved: true, damage: "CRACKED_SCREEN")]);

        missing.ShouldHaveSingleItem().Item.ShouldBe("PHOTO_OF_SERIAL_LABEL");
    }

    [Fact]
    public void Usable_photos_of_the_product_need_nothing_more()
    {
        EvidenceAgent.MissingItems([], [Photo(), Photo(visibleSerial: Serial)]).ShouldBeEmpty();
    }

    [Fact]
    public async Task Uninformative_photos_are_missing_information_not_risk_signals()
    {
        Script(photo: reference => PhotoJson(reference, showsProduct: false, quality: "POOR", consistency: "CANNOT_DETERMINE"));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        result.Output!.MissingItems.Select(m => m.Item).ShouldBe(["PHOTO_OF_DAMAGE", "PHOTO_OF_SERIAL_LABEL"]);
        EvidenceReadings.AiRiskSignals(result.Output, _references).ShouldBeEmpty();
        RiskAssessor.EvidenceSignals(Case(), EvidenceRiskFacts.From(result.Output, _references), Today).ShouldBeEmpty();
    }

    // ---- Failures -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(AiFailureKind.ProviderError, AgentStatus.Failed)]
    [InlineData(AiFailureKind.Timeout, AgentStatus.TimedOut)]
    public async Task A_failed_photo_call_does_not_throw_or_stop_the_other_files_and_fails_the_agent(AiFailureKind kind, AgentStatus expected)
    {
        Script(photo: reference => reference == "EV-3" ? null : PhotoJson(reference), failure: ScriptedModelProvider.Failed(kind, "boom"));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(expected);
        var evidence = result.Output.ShouldNotBeNull();
        evidence.Findings.Select(f => f.EvidenceId).ShouldBe([InvoiceId, Photo1Id]);
        _adjudication.Received(2).AddEvidenceFinding(Arg.Any<EvidenceFinding>());
        result.Diagnostics.ShouldContain(d => d.StartsWith("EV-3:", StringComparison.Ordinal) && d.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_photo_maps_to_Refused()
    {
        Script(photo: reference => reference == "EV-2" ? null : PhotoJson(reference), failure: ScriptedModelProvider.Stopped(AiStopKind.Refused));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Refused);
        result.Output!.Findings.Select(f => f.EvidenceId).ShouldBe([InvoiceId, Photo2Id]);
    }

    [Fact]
    public async Task An_invoice_extraction_that_breaks_the_schema_is_InvalidOutput_and_the_photos_are_still_analysed()
    {
        // The invoice answer breaks the schema again in the one corrective turn.
        Script(invoice: _ => """{"evidenceRef":"EV-1","legible":true}""", correctiveTurns: 1);

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        result.Output!.Findings.Select(f => f.Kind).ShouldBe([EvidenceFindingKind.PhotoAnalysis, EvidenceFindingKind.PhotoAnalysis]);
        result.Output.Validation.ShouldBeEmpty("legibility is unknown when the invoice was not read");
        _tools.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_photo_analysis_that_breaks_a_description_rule_is_InvalidOutput()
    {
        Script(photo: reference => reference == "EV-2" ? PhotoJson(reference, confidence: 140) : PhotoJson(reference));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        result.Diagnostics.ShouldContain(d => d.StartsWith("EV-2: /confidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_from_storage_fails_only_that_file()
    {
        _documents.OpenEvidenceAsync(Blob(Photo1Id), Arg.Any<CancellationToken>()).ThrowsAsync(new UnauthorizedAccessException("other tenant"));
        Script(photos: 1);

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Failed);
        result.Output!.Findings.Select(f => f.EvidenceId).ShouldBe([InvoiceId, Photo2Id]);
        result.Diagnostics.ShouldContain(d => d.StartsWith("EV-2: the file could not be read", StringComparison.Ordinal));
        _model.Requests.Count(r => r.Request.Route == "vision").ShouldBe(1);
    }

    [Fact]
    public async Task A_case_of_another_claim_is_rejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => RunAsync(Case() with { ClaimId = Guid.NewGuid() }));
    }

    // ---- Readings for the runner --------------------------------------------------------------------

    [Fact]
    public async Task The_result_maps_into_guardrail_facts_and_ai_risk_signals()
    {
        Script(photo: reference => reference == "EV-2"
            ? PhotoJson(reference, damageObserved: true, damage: "CRACKED_SCREEN", consistency: "INCONSISTENT")
            : PhotoJson(reference, serial: Serial, manipulation: true));

        var result = await RunAsync(Case());

        var facts = EvidenceReadings.ToGuardrailFacts(result.Output!, _references);
        facts.Photos.Select(p => (p.EvidenceRef, string.Join(',', p.DamageTypes))).ShouldBe([("EV-2", "CRACKED_SCREEN"), ("EV-3", "NONE_VISIBLE")]);
        facts.ConsistencyChecks.ShouldBe(result.Output!.ConsistencyChecks);
        facts.MissingItems.ShouldBeEmpty();

        var signals = EvidenceReadings.AiRiskSignals(result.Output!, _references);
        signals.Select(s => (s.Code, s.Source, string.Join(',', s.EvidenceRefs))).ShouldBe(
        [
            (RiskSignalCode.DamageInconsistentWithDescription, RiskSignalSource.Ai, "EV-2"),
            (RiskSignalCode.ManipulationAttempt, RiskSignalSource.Ai, "EV-3"),
        ]);
    }

    // ---- Replay -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_S1_evidence_fixtures_replay_through_the_agent_in_photo_order()
    {
        var replay = new ReplayModelProvider(
            new FixedScenario("S1"),
            new ReplayCallCounter(),
            Options.Create(new AiGatewayOptions { Mode = AiGatewayOptions.ReplayMode }),
            NullLogger<ReplayModelProvider>.Instance);

        var result = await RunAsync(Case(), replay, AiGatewayOptions.ReplayMode);

        result.Status.ShouldBe(AgentStatus.Succeeded, string.Join("; ", result.Diagnostics));
        result.Diagnostics.ShouldBeEmpty("each fixture names the reference the agent issued");
        var evidence = result.Output!;
        evidence.Validation.ShouldHaveSingleItem().Passed.ShouldBeTrue();
        evidence.Findings[0].Consistency.ShouldAllBe(c => c.Match);
        evidence.Findings[0].Consistency.Count.ShouldBe(5);
        evidence.Findings[1].EvidenceId.ShouldBe(Photo1Id);
        evidence.Findings[1].Confidence.ShouldBe(90);
        evidence.Findings[1].Consistency.ShouldBeEmpty();
        evidence.Findings[2].EvidenceId.ShouldBe(Photo2Id);
        evidence.Findings[2].Confidence.ShouldBe(92);
        evidence.Findings[2].Consistency.ShouldHaveSingleItem().Match.ShouldBeTrue();
        evidence.MissingItems.ShouldBeEmpty();
        EvidenceReadings.AiRiskSignals(evidence, _references).ShouldBeEmpty();
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private Task<AgentResult<EvidenceResult>> RunAsync(CaseContext input) => RunAsync(input, _model, AiGatewayOptions.LiveMode);

    private async Task<AgentResult<EvidenceResult>> RunAsync(CaseContext input, IModelProvider provider, string mode)
    {
        _services = Gateway(provider, mode);
        await using var scope = _services.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IAiGateway>();
        var run = new AdjudicationContext(RunId, new FakeTenantContext(Aurora), Case(), _references);
        var agent = new EvidenceAgent(new AgentTurnLoop(), new ContextBuilder(), new SchemaValidator(), _adjudication, _documents);
        var intake = IntakeResult.Create(RunId, Aurora, [], Extraction, []);

        return await agent.RunAsync(
            new EvidenceInput(input, intake), new AgentExecutionContext(run, gateway, _tools, new ActivityTraceWriter()), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Scripts the invoice turn and <paramref name="photos"/> photo turns, answering each from the file reference
    /// in the request (so the answers do not depend on call order). A null answer uses <paramref name="failure"/>.
    /// </summary>
    private void Script(
        Func<string, string?>? invoice = null, Func<string, string?>? photo = null, AiTurnResult? failure = null, int photos = 2, int correctiveTurns = 0)
    {
        for (var i = 0; i < 1 + photos + correctiveTurns; i++)
        {
            _model.Enqueue(request =>
            {
                var reference = AttachmentRef(request);
                var json = request.Request.Route == "vision"
                    ? (photo ?? (r => PhotoJson(r, serial: r == "EV-3" ? Serial : "NOT_VISIBLE", confidence: r == "EV-3" ? 92 : 90)))(reference)
                    : (invoice ?? (r => InvoiceJson(r)))(reference);
                return json is null ? failure! : ScriptedModelProvider.Completed(json);
            });
        }
    }

    private IReadOnlyList<string> AttachmentRefs(string route)
        => _model.Requests.Where(r => r.Request.Route == route).Select(AttachmentRef).ToList();

    private static string AttachmentRef(ResolvedTurnRequest request)
        => request.Messages[0].Parts.Select(p => p switch
        {
            ImagePart image => image.EvidenceRef,
            DocumentPart document => document.EvidenceRef,
            _ => null,
        }).OfType<string>().Single();

    private static string InvoiceJson(
        string reference, bool legible = true, string serial = Serial, decimal amount = 450m, string seller = "Aurora Store")
        => JsonSerializer.Serialize(new
        {
            evidenceRef = reference,
            legible,
            sellerName = seller,
            invoiceNumber = "AS-2026-104233",
            invoiceDate = legible ? "2026-06-04" : "UNKNOWN",
            productDescription = "Aurora Tab 10",
            modelCodeOnInvoice = legible ? ModelCode : "UNKNOWN",
            serialOnInvoice = serial,
            totalAmount = amount,
            currency = "USD",
            anomalies = Array.Empty<string>(),
            containsInstructionsToSystem = false,
        });

    private static string PhotoJson(
        string reference, string serial = "NOT_VISIBLE", bool showsProduct = true, bool damageObserved = false, string damage = "NONE_VISIBLE",
        string consistency = "CONSISTENT", string quality = "GOOD", int confidence = 90, bool manipulation = false)
        => JsonSerializer.Serialize(new
        {
            evidenceRef = reference,
            showsProduct,
            productTypeObserved = showsProduct ? "tablet" : "UNKNOWN",
            visibleSerial = serial,
            damageObserved,
            damageTypes = new[] { damage },
            consistentWithDescription = consistency,
            imageQuality = quality,
            containsInstructionsToSystem = manipulation,
            confidence,
            observations = "A tablet on a table.",
        });

    private static PhotoAnalysisOutput Photo(
        string visibleSerial = "NOT_VISIBLE", bool showsProduct = true, bool damageObserved = false, string damage = "NONE_VISIBLE",
        string consistency = "CONSISTENT", string quality = "GOOD")
        => new("EV-2", showsProduct, "tablet", visibleSerial, damageObserved, [damage], consistency, quality, false, 80, "A photo.");

    private static string Texts(IEnumerable<AiContentPart> parts) => string.Join('\n', parts.OfType<TextPart>().Select(p => p.Text));

    /// <summary>The real gateway (templates, schema validation, redaction) in front of <paramref name="provider"/>.</summary>
    private static ServiceProvider Gateway(IModelProvider provider, string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiGateway:Mode"] = mode,
            ["AiGateway:Routes:extraction:Provider"] = AnthropicModelProvider.ProviderName,
            ["AiGateway:Routes:extraction:Model"] = "claude-haiku-4-5",
            ["AiGateway:Routes:extraction:MaxTokens"] = "4000",
            ["AiGateway:Routes:extraction:TimeoutSeconds"] = "30",
            ["AiGateway:Routes:vision:Provider"] = AnthropicModelProvider.ProviderName,
            ["AiGateway:Routes:vision:Model"] = "claude-opus-5-5",
            ["AiGateway:Routes:vision:MaxTokens"] = "8000",
            ["AiGateway:Routes:vision:TimeoutSeconds"] = "30",
            ["AiGateway:Routes:embedding:Provider"] = HashEmbeddingGenerator.ProviderName,
            ["AiGateway:Routes:embedding:Model"] = "hash",
            ["AiGateway:Routes:embedding:Dimensions"] = "768",
            ["AiGateway:Anthropic:NoTraining"] = "true",
            ["AiGateway:RateLimits:PerTenantRequestsPerMinute"] = "600",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Aurora));
        services.AddScoped(_ => Substitute.For<IAiOpsRepository>());
        services.AddScoped(_ => Substitute.For<IClaimRepository>());
        services.AddWarrantyAiGateway(configuration);
        services.RemoveAll<IModelProvider>();
        services.AddSingleton(provider);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static CaseContext Case(IReadOnlyList<CaseEvidence>? evidence = null) => new(
        ClaimId, 1, "WC-2026-000002", ClaimChannel.ClaimantPortal, Today, PurchaseDate, "Aurora Store", 450m, "USD", Region.NA, ModelCode, Serial,
        Description, new CaseProduct(Guid.NewGuid(), ModelCode, "Aurora Tab 10", "tablet", 450m), new CaseCustomerView("US", Region.NA),
        evidence ?? [Evidence(InvoiceId, EvidenceKind.Invoice, "application/pdf"), Evidence(Photo1Id, EvidenceKind.Photo), Evidence(Photo2Id, EvidenceKind.Photo)],
        ClaimHistoryCounts.None, ReviewerInfoRequested: false, AutoInfoRequestCount: 0);

    private static CaseEvidence Evidence(Guid id, EvidenceKind kind, string contentType = "image/jpeg")
        => new(id, kind, kind == EvidenceKind.Invoice ? "invoice" : "photo", contentType, 2_048, new string('a', 64), 1, Blob(id));

    private static string Blob(Guid id) => $"claims/{ClaimId}/1/{id}";

    /// <summary>
    /// The evidence tools with deterministic answers: <c>invoice_validation</c> applies the real comparison
    /// rules to the test case; every call is recorded.
    /// </summary>
    private sealed class FakeEvidenceTools : IToolInvoker
    {
        private readonly ConcurrentQueue<AiToolCall> _calls = new();

        public bool FailInvoiceValidation { get; set; }

        public IReadOnlyList<AiToolCall> Calls => _calls.ToList();

        public IReadOnlyList<AiToolDefinition> Definitions { get; } =
        [
            new(ToolNames.InvoiceValidation, "Compares invoice values with the claim.", Json("""{"type":"object","properties":{}}""")),
            new(ToolNames.ProductLookup, "Looks up a product.", Json("""{"type":"object","properties":{}}""")),
        ];

        public Task<AiToolResult> InvokeAsync(AiToolCall call, CancellationToken ct)
        {
            _calls.Enqueue(call);
            object content;
            var isError = false;
            if (call.ToolName == ToolNames.InvoiceValidation && !FailInvoiceValidation)
            {
                InvoiceValidationTool.TryReadInvoice(call.Arguments.GetProperty("extracted"), out var fields, out _).ShouldBeTrue();
                content = new InvoiceValidationResult(
                    call.Arguments.GetProperty("invoiceRef").GetString()!, InvoiceValidationTool.Compare(ClaimedPurchase.From(Case()), fields));
            }
            else if (call.ToolName == ToolNames.ProductLookup)
            {
                content = new ProductLookupResult(true, true, "Aurora Tab 10", "tablet");
            }
            else
            {
                content = new { error = "Tool unavailable." };
                isError = true;
            }

            return Task.FromResult(new AiToolResult(call.CallId, JsonSerializer.SerializeToElement(content, ToolJson.Options), isError));
        }

        private static JsonElement Json(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    /// <summary>
    /// Holds every photo call until all of them have arrived (so sequential calls would time out) and then
    /// answers the first photo last; records arrival and completion order.
    /// </summary>
    private sealed class ParallelProbe(int photos) : IModelProvider
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _arrivals = new();
        private readonly ConcurrentQueue<string> _completions = new();
        private int _photoCalls;

        public string Name => AnthropicModelProvider.ProviderName;

        public IReadOnlyList<string> PhotoArrivals => _arrivals.ToList();

        public IReadOnlyList<string> PhotoCompletions => _completions.ToList();

        public async Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct)
        {
            var reference = AttachmentRef(request);
            if (request.Request.Route != "vision")
            {
                return ScriptedModelProvider.Completed(InvoiceJson(reference));
            }

            _arrivals.Enqueue(reference);
            if (Interlocked.Increment(ref _photoCalls) == photos)
            {
                _allArrived.TrySetResult();
            }

            await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (reference == "EV-2")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }

            _completions.Enqueue(reference);
            return ScriptedModelProvider.Completed(PhotoJson(reference, serial: reference == "EV-3" ? Serial : "NOT_VISIBLE"));
        }
    }

    private sealed class FixedScenario(string scenario) : IReplayScenarioSelector
    {
        public Task<string?> SelectScenarioAsync(AiCallContext context, CancellationToken ct) => Task.FromResult<string?>(scenario);

        public Task<IReadOnlyDictionary<string, string>> GetFixtureVariablesAsync(AiCallContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ReplayFixtureVariables.PurchaseDate] = "2026-06-04",
                [ReplayFixtureVariables.ClaimDate] = "2026-10-04",
            });
    }
}
