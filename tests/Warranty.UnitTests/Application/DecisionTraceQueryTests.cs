using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Trace;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Audit;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Policies;

namespace Warranty.UnitTests.Application;

/// <summary>The decision trace read model (T069, FR-038 – FR-041): ordering, step details, resolved references, privacy.</summary>
public sealed class DecisionTraceQueryTests : IDisposable
{
    private const string Email = "jordan.sample@example.test";
    private const string Phone = "+1 (555) 010-0042";
    private const string FullName = "Jordan Sample";
    private const string Address = "12 Maple Street";
    private const string Correlation = "4bf92f3577b34da6a3ce929d0e0e4736";

    private static readonly Guid TenantId = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly IDecisionTrailReader _trail = Substitute.For<IDecisionTrailReader>();
    private readonly TenantContextScope _tenant = TenantContextScope.Begin(TenantId, "aurora", "auditor-sub");
    private readonly Claim _claim;
    private readonly Guid _runId = Guid.CreateVersion7();
    private readonly ClaimEvidence _invoice;
    private readonly ClaimEvidence _photo;
    private readonly List<DecisionTrailEntry> _entries = [];

    public DecisionTraceQueryTests()
    {
        _claim = Claim.Submit(
            Guid.CreateVersion7(), TenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, Guid.CreateVersion7(),
            Email, Phone, "AUR-TAB10", ProductId, "AT10-99-0042", new DateOnly(2026, 6, 1), "Aurora Store", 450m, Region.NA,
            "The tablet stopped turning on.", Start);
        _invoice = ClaimEvidence.Create(Guid.CreateVersion7(), TenantId, _claim.Id, 1, EvidenceKind.Invoice, "invoice.pdf", "application/pdf", 1000, new string('a', 64), Start);
        _photo = ClaimEvidence.Create(Guid.CreateVersion7(), TenantId, _claim.Id, 1, EvidenceKind.Photo, "photo.jpg", "image/jpeg", 2000, new string('b', 64), Start);

        _claims.GetAsync(_claim.Id, Arg.Any<CancellationToken>()).Returns(_claim);
        _claims.GetEvidenceAsync(_claim.Id, Arg.Any<CancellationToken>()).Returns([_invoice, _photo]);
        _customers.GetAsync(_claim.CustomerId, Arg.Any<CancellationToken>())
            .Returns(Customer.Create(_claim.CustomerId, TenantId, FullName, Email, "US", Phone, Address));
        _trail.ReadAsync(_claim.Id, Arg.Any<CancellationToken>()).Returns(_ => new DecisionTrailSnapshot(_entries, true));
        _adjudication.GetRunRecordAsync(_runId, Arg.Any<CancellationToken>()).Returns(RunRecord());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenant.Dispose();

    [Fact]
    public async Task Entries_are_returned_in_seq_order_with_wire_step_names_actor_summary_and_correlation_id()
    {
        Add(TrailStep.ClaimSubmitted, Claim.ClaimantSubmitter, "Claim submitted through the claimant channel.", new { round = 1 });
        Add(TrailStep.TenantResolved, "system", "Tenant aurora resolved from the claimant channel host.", new { tenantSlug = "aurora" });
        Add(TrailStep.IntakeValidated, "intake", "Intake validation passed: 2 checks, nothing missing.", new
        {
            runId = _runId, round = 1, checks = new[] { new { check = "INVOICE_PRESENT", passed = true, detail = (string?)null } },
            missingItems = Array.Empty<string>(), shortCircuit = false,
        });
        _entries.Reverse();

        var trace = (await Query().GetAsync(_claim.Id, Ct))!;

        trace.ClaimId.ShouldBe(_claim.Id);
        trace.Integrity.HashChainValid.ShouldBeTrue();
        trace.Entries.Select(e => e.Seq).ShouldBe([1, 2, 3]);
        trace.Entries.Select(e => e.Step).ShouldBe(["ClaimSubmitted", "TenantResolved", "IntakeValidated"]);
        trace.Entries.Select(e => e.Actor).ShouldBe([Claim.ClaimantSubmitter, "system", "intake"]);
        trace.Entries[2].Summary.ShouldBe("Intake validation passed: 2 checks, nothing missing.");
        trace.Entries.ShouldAllBe(e => e.CorrelationId == Correlation);
        trace.Entries[2].Details!["checks"]![0]!["check"]!.GetValue<string>().ShouldBe("INVOICE_PRESENT");
        trace.Entries[2].Details!["runId"]!.GetValue<string>().ShouldBe(_runId.ToString());
    }

    [Fact]
    public async Task A_broken_hash_chain_is_reported()
    {
        Add(TrailStep.ClaimSubmitted, Claim.ClaimantSubmitter, "Claim submitted.", new { round = 1 });
        _trail.ReadAsync(_claim.Id, Arg.Any<CancellationToken>()).Returns(new DecisionTrailSnapshot(_entries, false));

        var trace = (await Query().GetAsync(_claim.Id, Ct))!;

        trace.Integrity.HashChainValid.ShouldBeFalse();
        trace.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_claim_or_a_claim_of_another_tenant_has_no_trace()
    {
        var other = Claim.Submit(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter,
            Guid.CreateVersion7(), Email, null, "AUR-TAB10", null, "AT10-99-0043", new DateOnly(2026, 6, 1), "Aurora Store", 450m, Region.NA,
            "The tablet stopped turning on.", Start);
        _claims.GetAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);

        (await Query().GetAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
        (await Query().GetAsync(other.Id, Ct)).ShouldBeNull();
        await _trail.DidNotReceiveWithAnyArgs().ReadAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retrieved_clauses_get_their_effective_dates_and_citation_from_the_stored_references()
    {
        Add(TrailStep.PolicyRetrieved, "policy", "Policy applies; 2 clauses issued.", new
        {
            runId = _runId, round = 1, versionOutcome = "OK",
            clauses = new[]
            {
                new { refId = "POL-1", clauseKey = "AUR-WP-2.1" },
                new { refId = "POL-2", clauseKey = "AUR-WP-3.1" },
                new { refId = "POL-9", clauseKey = "AUR-WP-9.9" },
            },
        });

        var details = (await Query().GetAsync(_claim.Id, Ct))!.Entries.Single().Details!;

        var clauses = details["clauses"]!.AsArray();
        clauses[0]!["effectiveFrom"]!.GetValue<string>().ShouldBe("2025-01-01");
        clauses[0]!["cited"]!.GetValue<bool>().ShouldBeTrue();
        clauses[1]!["cited"]!.GetValue<bool>().ShouldBeFalse();
        clauses[2]!.AsObject().ContainsKey("cited").ShouldBeFalse();
    }

    [Fact]
    public async Task The_recommendation_resolves_policy_and_evidence_references_from_stored_run_data_and_adds_the_reasoning()
    {
        Add(TrailStep.AiRecommended, "decision", "AI recommends APPROVE (COVERED, confidence 92).", new
        {
            runId = _runId, round = 1, isValid = true, decision = "APPROVE", coverage = "COVERED", confidence = 92,
            evidenceRefs = new[] { "EV-1", "EV-2", "EV-7" },
            policyRefs = new[] { new { refId = "POL-1", relevance = "SUPPORTS_COVERAGE" }, new { refId = "POL-5", relevance = "CONTEXT" } },
        });

        var details = (await Query().GetAsync(_claim.Id, Ct))!.Entries.Single().Details!;

        details["reasoningSummary"]!.GetValue<string>().ShouldBe("Battery failure within the 12-month period.");
        var policyRefs = details["policyRefs"]!.AsArray();
        policyRefs[0]!["resolved"]!.GetValue<bool>().ShouldBeTrue();
        policyRefs[0]!["clauseKey"]!.GetValue<string>().ShouldBe("AUR-WP-2.1");
        policyRefs[0]!["documentTitle"]!.GetValue<string>().ShouldBe("Aurora Warranty Policy");
        policyRefs[0]!["version"]!.GetValue<int>().ShouldBe(2);
        policyRefs[0]!["clauseType"]!.GetValue<string>().ShouldBe("Coverage");
        policyRefs[1]!["resolved"]!.GetValue<bool>().ShouldBeFalse();

        var evidence = details["evidence"]!.AsArray();
        evidence.Count.ShouldBe(3);
        evidence[0]!["evidenceId"]!.GetValue<Guid>().ShouldBe(_invoice.Id);
        evidence[0]!["kind"]!.GetValue<string>().ShouldBe("Invoice");
        evidence[0]!["observation"]!.GetValue<string>().ShouldBe("Invoice dated within coverage.");
        evidence[1]!["evidenceId"]!.GetValue<Guid>().ShouldBe(_photo.Id);
        evidence[2]!["resolved"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Guardrail_checks_carry_expected_actual_and_message_from_the_stored_evaluation()
    {
        Add(TrailStep.GuardrailsEvaluated, "system", "Guardrails: 2 of 2 checks passed; disposition AUTO_APPROVE.", new
        {
            runId = _runId, round = 1, disposition = "AUTO_APPROVE", reasons = Array.Empty<string>(),
            checks = new[] { new { code = "SCHEMA_VALID", stage = "pre", passed = true } },
        });

        var details = (await Query().GetAsync(_claim.Id, Ct))!.Entries.Single().Details!;

        var checks = details["checks"]!.AsArray();
        checks.Count.ShouldBe(2);
        checks[1]!["code"]!.GetValue<string>().ShouldBe("COVERAGE_WINDOW_AGREES");
        checks[1]!["expected"]!.GetValue<string>().ShouldBe("within");
        checks[1]!["actual"]!.GetValue<string>().ShouldBe("within");
        checks[1]!["message"]!.GetValue<string>().ShouldBe("Claim date 2026-10-01 is before 2027-06-01.");
        details["disposition"]!.GetValue<string>().ShouldBe("AUTO_APPROVE");
    }

    [Fact]
    public async Task A_run_of_another_claim_is_never_used_to_resolve_references()
    {
        var foreignRun = Guid.CreateVersion7();
        var foreign = AdjudicationRun.Start(foreignRun, TenantId, Guid.CreateVersion7(), 1, Correlation, Start);
        _adjudication.GetRunRecordAsync(foreignRun, Arg.Any<CancellationToken>())
            .Returns(new RunRecord(foreign, null, [], [PolicyRef(foreignRun, "POL-1", "OTHER-1")], null, null, null, null));
        Add(TrailStep.AiRecommended, "decision", "AI recommends APPROVE.", new
        {
            runId = foreignRun, round = 1, evidenceRefs = Array.Empty<string>(), policyRefs = new[] { new { refId = "POL-1", relevance = "CONTEXT" } },
        });

        var details = (await Query().GetAsync(_claim.Id, Ct))!.Entries.Single().Details!;

        details["policyRefs"]![0]!.AsObject().ContainsKey("clauseKey").ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_steps_and_unusual_payloads_are_tolerated()
    {
        _entries.Add(Entry(1, (TrailStep)999, "system", "A step from a newer build.", "{\"note\":\"kept\"}"));
        _entries.Add(Entry(2, TrailStep.ActionExecuted, "adjudication-service", "Simulated customer notification queued.", "[1,2]"));
        _entries.Add(Entry(3, TrailStep.Correction, "system", "Correction appended.", "not json"));
        _entries.Add(Entry(4, TrailStep.AiStepFailed, "evidence", "The evidence step did not complete (Timeout).", "{\"runId\":\"not-a-guid\",\"agent\":\"evidence\"}"));

        var trace = (await Query().GetAsync(_claim.Id, Ct))!;

        trace.Entries.Select(e => e.Step).ShouldBe(["999", "ActionExecuted", "Correction", "AiStepFailed"]);
        trace.Entries[0].Details!["note"]!.GetValue<string>().ShouldBe("kept");
        trace.Entries[1].Details!["value"]!.AsArray().Count.ShouldBe(2);
        trace.Entries[2].Details.ShouldBeNull();
        trace.Entries[3].Details!["agent"]!.GetValue<string>().ShouldBe("evidence");
    }

    [Fact]
    public async Task No_customer_identifier_appears_anywhere_in_the_trace()
    {
        Add(TrailStep.ClaimSubmitted, Claim.ClaimantSubmitter, $"Claim submitted by {FullName} ({Email}).", new
        {
            round = 1, contactEmail = Email, contactPhone = Phone, customerName = FullName, address = Address, productModelCode = "AUR-TAB10",
        });
        Add(TrailStep.IntakeValidated, "intake", "Intake validation passed.", new
        {
            runId = _runId, round = 1,
            checks = new[] { new { check = "CONTACT_PRESENT", passed = true, detail = $"Reach {FullName} at {Phone} or {Address}." } },
        });

        var trace = (await Query().GetAsync(_claim.Id, Ct))!;

        var json = JsonSerializer.Serialize(trace, JsonSerializerOptions.Web);
        foreach (var identifier in new[] { Email, FullName, "Jordan", "Sample", Address, "Maple", Phone, "010-0042" })
        {
            json.ShouldNotContain(identifier, Case.Insensitive);
        }

        trace.Entries[0].Details!["productModelCode"]!.GetValue<string>().ShouldBe("AUR-TAB10");
        json.ShouldContain("[CUSTOMER]");
    }

    [Fact]
    public async Task The_serialized_trace_follows_the_contract_property_names()
    {
        Add(TrailStep.ClaimSubmitted, Claim.ClaimantSubmitter, "Claim submitted.", new { round = 1 });

        var trace = (await Query().GetAsync(_claim.Id, Ct))!;
        var json = JsonNode.Parse(JsonSerializer.Serialize(trace, JsonSerializerOptions.Web))!.AsObject();

        json.Select(p => p.Key).ShouldBe(["claimId", "integrity", "entries"], ignoreOrder: true);
        json["integrity"]!["hashChainValid"]!.GetValue<bool>().ShouldBeTrue();
        json["entries"]![0]!.AsObject().Select(p => p.Key)
            .ShouldBe(["seq", "occurredAt", "step", "actor", "summary", "correlationId", "details"], ignoreOrder: true);
    }

    private DecisionTraceQuery Query() => new(_tenant, _claims, _customers, _adjudication, _trail);

    private void Add(TrailStep step, string actor, string summary, object payload)
        => _entries.Add(Entry(_entries.Count + 1, step, actor, summary, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)));

    private DecisionTrailEntry Entry(int seq, TrailStep step, string actor, string summary, string payloadJson)
        => DecisionTrailEntry.Create(
            TenantId, _claim.Id, seq, Start.AddSeconds(seq), step, actor, summary, payloadJson, Correlation, new string('0', 64), new string('1', 64));

    private RunRecord RunRecord()
    {
        var run = AdjudicationRun.Start(_runId, TenantId, _claim.Id, 1, Correlation, Start);
        run.RecordReferences(new Dictionary<string, Guid> { ["EV-1"] = _invoice.Id, ["EV-2"] = _photo.Id, ["POL-1"] = Guid.CreateVersion7() });
        var cited = PolicyRef(_runId, "POL-1", "AUR-WP-2.1");
        cited.MarkCited();
        var recommendation = Recommendation.CreateValid(
            _runId, TenantId, "{}", AiDecision.Approve, CoverageDetermination.Covered, 92, "Battery failure within the 12-month period.",
            "Your claim is approved.", [new EvidenceCitation("EV-1", "Invoice dated within coverage."), new EvidenceCitation("EV-2", "Swollen battery.")],
            [new PolicyCitation("POL-1", PolicyRefRelevance.SupportsCoverage)], [], false, "model", "decision", "1.0.0");
        var guardrails = GuardrailEvaluation.Create(
            _runId, TenantId,
            [
                new GuardrailCheck(GuardrailCheckCode.SchemaValid, "pre", true, null, null, null),
                new GuardrailCheck(GuardrailCheckCode.CoverageWindowAgrees, "post", true, "within", "within", "Claim date 2026-10-01 is before 2027-06-01."),
            ],
            Disposition.AutoApprove, [], null, Start);
        return new RunRecord(run, null, [], [cited, PolicyRef(_runId, "POL-2", "AUR-WP-3.1")], null, null, recommendation, guardrails);
    }

    private static RetrievedPolicyRef PolicyRef(Guid runId, string refId, string clauseKey)
        => RetrievedPolicyRef.Create(
            Guid.CreateVersion7(), TenantId, runId, refId, Guid.CreateVersion7(), Guid.CreateVersion7(), clauseKey, ClauseType.Coverage, null,
            "Aurora Warranty Policy", 2, new DateOnly(2025, 1, 1), null, 0.9f);
}
