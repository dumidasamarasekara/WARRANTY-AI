using System.Globalization;
using System.Text;
using System.Text.Json;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Agents;

/// <summary>
/// The Decision Agent (contracts/agents-and-tools.md). It assembles the user turn from everything the
/// earlier steps produced — case facts, the intake reading, the evidence findings with their <c>EV-n</c>,
/// the policy assessment and every issued <c>POL-n</c> clause (Period/Exclusion first), the deterministic
/// coverage window and the deterministic risk signals — and asks the <c>adjudication</c> route for a
/// <c>decision-recommendation</c>. Before the model call it runs <c>claim_history_lookup</c> itself
/// through the run's scoped invoker; the model is offered that tool and <c>search_global_knowledge</c>.
/// </summary>
/// <remarks>
/// The output is validated with <see cref="SchemaValidator"/> (schema plus description rules) and the
/// run's <see cref="ReferenceRegistry"/>: every cited <c>EV-n</c> and <c>POL-n</c> must have been issued
/// for this run, and a <c>GLB-n</c> is never accepted as policy. A recommendation is stored in either
/// case (<c>adjudication.recommendations</c>, unit of work only; the runner commits): invalid output is
/// stored with <c>is_valid = false</c> and its errors and the agent returns
/// <see cref="AgentStatus.InvalidOutput"/> with that recommendation, so the guardrails see
/// <c>INVALID_RECOMMENDATION</c>. Refusals, timeouts and other failures store nothing and return no
/// output (<c>AI_UNAVAILABLE</c>). The raw output is stored after <see cref="IPiiRedactor"/> masked every
/// string in it, and the recommendation's texts are read from that redacted copy. The agent never
/// screens the claimant explanation: that is the guardrail check <c>CLAIMANT_TEXT_SAFE</c>.
/// </remarks>
public sealed class DecisionAgent(
    AgentTurnLoop loop,
    ContextBuilder contextBuilder,
    SchemaValidator schemas,
    IAdjudicationRepository adjudication,
    IPiiRedactor redactor) : IAgent<DecisionInput, RecommendationResult>
{
    public const string Route = "adjudication";

    /// <summary>The Decision Agent's descriptor (budgets: contracts/agents-and-tools.md).</summary>
    public static AgentDescriptor Agent { get; } = new(
        AgentNames.Decision,
        Route,
        new PromptRef("decision", 1),
        "warranty-ai/decision-recommendation/v1",
        [ToolNames.ClaimHistoryLookup, ToolNames.SearchGlobalKnowledge],
        MaxTurns: 6,
        InputTokenBudget: 24_000);

    /// <summary>The only variable of the <c>decision</c> prompt template.</summary>
    private static readonly IReadOnlyDictionary<string, string> PromptVariables =
        new Dictionary<string, string>(StringComparer.Ordinal) { [UntrustedContent.PreambleVariable] = UntrustedContent.Preamble };

    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);

    public AgentDescriptor Descriptor => Agent;

    public async Task<AgentResult<RecommendationResult>> RunAsync(DecisionInput input, AgentExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);
        if (input.Case.ClaimId != ctx.Run.ClaimId)
        {
            throw new ArgumentException("The case is not the run's claim.", nameof(input));
        }

        using var span = ctx.Trace.StartSpan($"agent.{Agent.Name}", new Dictionary<string, object?> { ["agent"] = Agent.Name, ["claim_id"] = input.Case.ClaimId });
        var diagnostics = new List<string>();

        var history = await LookupHistoryAsync(ctx, diagnostics, ct) ?? input.Case.History;
        var context = contextBuilder.Build(ContextItems(input, ctx.Run.References, history), Agent.InputTokenBudget);
        if (context.IsOverflow)
        {
            return AgentResult<RecommendationResult>.Failure(
                AgentStatus.Failed,
                [.. diagnostics, string.Create(CultureInfo.InvariantCulture, $"context_overflow: the required context needs ~{context.EstimatedTokens} tokens; the budget is {context.Budget}.")]);
        }

        if (context.Dropped.Count > 0)
        {
            diagnostics.Add($"context: dropped {string.Join(", ", context.Dropped)} to fit the token budget.");
        }

        var conversation = new AiConversation();
        conversation.AddUser([.. context.Parts]);
        var outcome = await loop.RunAsync(
            new AgentTurnLoopRequest(Agent, conversation, PromptVariables, schemas.GetOutputSchema(Agent.OutputSchemaId!), AgentTurnLoopRequest.DefaultTurnTimeout),
            ctx,
            ct);

        var turn = outcome.LastTurn;
        if (outcome.Status == AgentStatus.Succeeded && turn.StructuredOutput is { } output)
        {
            return Evaluate(input, output, turn.Usage.Model, ctx.Run, diagnostics);
        }

        if (outcome.Status is AgentStatus.Succeeded or AgentStatus.InvalidOutput)
        {
            // The gateway rejected the output (not JSON or not the schema): store it as an invalid recommendation.
            IReadOnlyList<string> errors = turn.Failure?.ValidationErrors is { Count: > 0 } validation
                ? validation
                : [turn.Failure?.Message ?? "The model returned no structured output."];
            var raw = RedactedText(string.Concat(turn.AssistantMessage.Parts.OfType<TextPart>().Select(p => p.Text)));
            var invalid = Invalid(ctx.Run, raw.Text, errors, turn.Usage.Model, raw.Json);
            adjudication.AddRecommendation(invalid);
            return new AgentResult<RecommendationResult>(
                AgentStatus.InvalidOutput, new RecommendationResult(invalid, AiRiskReading.None), [.. diagnostics, .. errors]);
        }

        var failure = turn.Failure;
        var message = $"{outcome.Outcome}: {failure?.Message ?? $"the model stopped with '{turn.Stop}'."}";
        return AgentResult<RecommendationResult>.Failure(outcome.Status, [.. diagnostics, message, .. failure?.ValidationErrors ?? []]);
    }

    /// <summary>
    /// The user turn's items: instructions; case facts, claimant description, intake, each evidence file,
    /// policy assessment, coverage window and risk signals (all required); Period/Exclusion clauses;
    /// other clauses by score; global snippets issued earlier in the run (dropped first). No item carries a
    /// customer identifier (FR-006a): the case holds only the redacted customer view.
    /// </summary>
    public static IReadOnlyList<ContextItem> ContextItems(DecisionInput input, ReferenceRegistry references, ClaimHistoryCounts history)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(history);

        var items = new List<ContextItem>
        {
            ContextItem.Text(
                "instructions",
                ContextPriority.Instructions,
                "Recommend an outcome for this claim from the material below. Cite only the EV-n and POL-n IDs listed here. "
                + "Return only the decision recommendation."),
            ContextItem.Text("case-facts", ContextPriority.CaseFacts, CaseFacts(input.Case, history)),
            new("description", ContextPriority.CaseFacts, [UntrustedContent.ClaimantDescription(input.Case.ProblemDescription ?? string.Empty)]),
            ContextItem.Text("intake", ContextPriority.CaseFacts, IntakeText(input.Intake)),
        };

        items.AddRange(EvidenceItems(input, references));
        items.Add(ContextItem.Text("policy-assessment", ContextPriority.CaseFacts, PolicyText(input)));
        items.Add(ContextItem.Text("coverage-window", ContextPriority.CaseFacts, CoverageText(input.CoverageWindow)));
        items.Add(ContextItem.Text("risk-signals", ContextPriority.CaseFacts, SignalsText(input.DeterministicSignals)));

        foreach (var clause in input.PolicyClauses.DistinctBy(c => c.RefId, StringComparer.Ordinal))
        {
            var decisive = clause.ClauseType is ClauseType.Period or ClauseType.Exclusion;
            references.TryResolve(clause.RefId, out var entry);
            items.Add(ContextItem.Text(
                $"clause {clause.RefId}",
                decisive ? ContextPriority.DecisiveClauses : ContextPriority.OtherClauses,
                ClauseText(clause, entry?.Chunk),
                clause.Score));
        }

        foreach (var global in references.Entries.Where(e => e.Kind == ReferenceKind.Global && e.Chunk is not null))
        {
            items.Add(ContextItem.Text(
                $"global {global.Id}",
                ContextPriority.GlobalSnippets,
                $"{global.Id} (background only; never cite it as evidence or policy) - {global.Chunk!.DocumentTitle}:\n{global.Chunk.Text}",
                global.Chunk.Score));
        }

        return items;
    }

    // ---- Output validation -------------------------------------------------------------------------

    private AgentResult<RecommendationResult> Evaluate(
        DecisionInput input, JsonElement output, string model, AdjudicationContext run, List<string> diagnostics)
    {
        var errors = new List<string>(schemas.Validate(Agent.OutputSchemaId!, output).Errors);
        var raw = RedactJson(output);
        using var document = JsonDocument.Parse(raw);
        var redacted = document.RootElement;

        DecisionOutput? parsed = null;
        if (errors.Count == 0)
        {
            try
            {
                parsed = redacted.Deserialize<DecisionOutput>(OutputJson);
            }
            catch (JsonException ex)
            {
                errors.Add($": the output could not be read ({ex.Message}).");
            }
        }

        var risk = AiRiskReading.None;
        if (parsed is not null)
        {
            errors.AddRange(ReferenceErrors(parsed, run.References));
            errors.AddRange(MappingErrors(parsed));
            risk = RiskOf(parsed, run.References);
        }

        if (errors.Count > 0 || parsed is null)
        {
            var invalid = Invalid(run, raw, errors.Count > 0 ? errors : [": the output could not be read."], model, redacted);
            adjudication.AddRecommendation(invalid);
            return new AgentResult<RecommendationResult>(AgentStatus.InvalidOutput, new RecommendationResult(invalid, risk), [.. diagnostics, .. errors]);
        }

        var recommendation = Recommendation.CreateValid(
            run.RunId,
            run.Tenant.TenantId,
            raw,
            DecisionOf(parsed.Decision)!.Value,
            CoverageOf(parsed.Coverage)!.Value,
            parsed.Confidence,
            parsed.ReasoningSummary,
            parsed.ClaimantExplanation,
            parsed.EvidenceRefs.Select(r => new EvidenceCitation(r.Ref, r.Observation)),
            parsed.PolicyRefs.Select(r => new PolicyCitation(r.Ref, RelevanceOf(r.Relevance)!.Value)),
            parsed.MissingInformation.Select(m => RequestedItem.Create(m.Item, m.Reason)),
            parsed.ManipulationDetected,
            model,
            Agent.Prompt.Id,
            Agent.Prompt.Version.ToString(CultureInfo.InvariantCulture));
        adjudication.AddRecommendation(recommendation);

        var cited = parsed.PolicyRefs.Select(r => r.Ref).ToHashSet(StringComparer.Ordinal);
        foreach (var clause in input.PolicyClauses.Where(c => cited.Contains(c.RefId)))
        {
            clause.MarkCited();
        }

        return AgentResult<RecommendationResult>.Success(new RecommendationResult(recommendation, risk), [.. diagnostics]);
    }

    /// <summary>One error per cited ID that was not issued for this run or is of the wrong kind (FR-022).</summary>
    private static IEnumerable<string> ReferenceErrors(DecisionOutput output, ReferenceRegistry references)
    {
        for (var i = 0; i < output.EvidenceRefs.Count; i++)
        {
            foreach (var error in references.Validate([output.EvidenceRefs[i].Ref], ReferenceKind.Evidence))
            {
                yield return Invariant($"/evidenceRefs/{i}/ref: {error}");
            }
        }

        for (var i = 0; i < output.PolicyRefs.Count; i++)
        {
            foreach (var error in references.Validate([output.PolicyRefs[i].Ref], ReferenceKind.Policy))
            {
                yield return Invariant($"/policyRefs/{i}/ref: {error}");
            }
        }

        for (var i = 0; i < output.Risk.Signals.Count; i++)
        {
            var refs = output.Risk.Signals[i].EvidenceRefs;
            for (var j = 0; j < refs.Count; j++)
            {
                foreach (var error in references.Validate([refs[j]], ReferenceKind.Evidence))
                {
                    yield return Invariant($"/risk/signals/{i}/evidenceRefs/{j}: {error}");
                }
            }
        }
    }

    /// <summary>Values the Domain cannot hold (the schema already restricts the enums).</summary>
    private static IEnumerable<string> MappingErrors(DecisionOutput output)
    {
        if (DecisionOf(output.Decision) is null)
        {
            yield return $"/decision: '{output.Decision}' is not a decision.";
        }

        if (CoverageOf(output.Coverage) is null)
        {
            yield return $"/coverage: '{output.Coverage}' is not a coverage reading.";
        }

        for (var i = 0; i < output.PolicyRefs.Count; i++)
        {
            if (RelevanceOf(output.PolicyRefs[i].Relevance) is null)
            {
                yield return Invariant($"/policyRefs/{i}/relevance: '{output.PolicyRefs[i].Relevance}' is not a relevance.");
            }
        }

        for (var i = 0; i < output.MissingInformation.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(output.MissingInformation[i].Item) || string.IsNullOrWhiteSpace(output.MissingInformation[i].Reason))
            {
                yield return Invariant($"/missingInformation/{i}: the item and its reason must not be empty.");
            }
        }
    }

    /// <summary>
    /// The model's risk reading: its level (display only) and its signals as AI-sourced signals with the
    /// fixed AI severity. Only issued <c>EV-n</c> IDs are kept on a signal. <c>manipulationDetected</c>
    /// without a listed <c>MANIPULATION_ATTEMPT</c> signal still yields one.
    /// </summary>
    private static AiRiskReading RiskOf(DecisionOutput output, ReferenceRegistry references)
    {
        var signals = new List<RiskSignal>();
        foreach (var signal in output.Risk.Signals)
        {
            if (SignalCodeOf(signal.Code) is not { } code)
            {
                continue;
            }

            signals.Add(new RiskSignal(
                code,
                RiskSignalSource.Ai,
                RiskAssessor.SeverityOf(code, RiskSignalSource.Ai),
                signal.Description,
                signal.EvidenceRefs.Where(r => references.Validate([r], ReferenceKind.Evidence).Count == 0).Distinct(StringComparer.Ordinal).ToArray()));
        }

        if (output.ManipulationDetected && signals.All(s => s.Code != RiskSignalCode.ManipulationAttempt))
        {
            signals.Add(new RiskSignal(
                RiskSignalCode.ManipulationAttempt,
                RiskSignalSource.Ai,
                RiskAssessor.SeverityOf(RiskSignalCode.ManipulationAttempt, RiskSignalSource.Ai),
                "The Decision Agent reported an instruction attempt in the claim content.",
                []));
        }

        var level = output.Risk.Level switch
        {
            "LOW" => RiskLevel.Low,
            "MEDIUM" => RiskLevel.Medium,
            "HIGH" => (RiskLevel?)RiskLevel.High,
            _ => null,
        };
        return new AiRiskReading(level, signals);
    }

    private static Recommendation Invalid(AdjudicationContext run, string raw, IReadOnlyList<string> errors, string model, JsonElement? output)
    {
        AiDecision? decision = null;
        int? confidence = null;
        if (output is { ValueKind: JsonValueKind.Object } json)
        {
            if (json.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String)
            {
                decision = DecisionOf(d.GetString());
            }

            if (json.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var value) && value is >= 0 and <= 100)
            {
                confidence = value;
            }
        }

        return Recommendation.CreateInvalid(
            run.RunId, run.Tenant.TenantId, raw, errors, model, Agent.Prompt.Id, Agent.Prompt.Version.ToString(CultureInfo.InvariantCulture), decision, confidence);
    }

    // ---- Redaction ---------------------------------------------------------------------------------

    /// <summary>The output with every property name and string value passed through the PII redactor; structure and numbers unchanged.</summary>
    private string RedactJson(JsonElement output)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(output, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(redactor.Redact(property.Name).Text);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(redactor.Redact(element.GetString()!).Text);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>Raw model text: redacted as JSON when it parses (the parsed copy is returned too), else as plain text.</summary>
    private (string Text, JsonElement? Json) RedactedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var redacted = RedactJson(document.RootElement);
            using var parsed = JsonDocument.Parse(redacted);
            return (redacted, parsed.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (redactor.Redact(text).Text, null);
        }
    }

    // ---- Context text ------------------------------------------------------------------------------

    /// <summary>Product, dates, price and claim-history counts only — never customer identifiers (FR-006a).</summary>
    private static string CaseFacts(CaseContext input, ClaimHistoryCounts history)
    {
        var region = input.Region ?? input.Customer.Region;
        var facts = new StringBuilder("Case facts:\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Product model code: {input.ProductModelCode}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Serial number: {input.SerialNumber}\n");
        facts.Append(input.Product is { } product
            ? string.Create(CultureInfo.InvariantCulture, $"- Product: {product.Name} (category: {product.Category})\n")
            : "- Product: not found in the catalog\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Region: {region?.ToString() ?? "unknown"}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Purchase date: {Iso(input.PurchaseDate)}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Claim date: {Iso(input.ClaimDate)}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Purchase price: {input.PurchasePrice:0.00} {input.Currency}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Claim round: {input.Round}\n");
        facts.Append("- Claim history for this serial (claim_history_lookup): ");
        facts.Append(CultureInfo.InvariantCulture, $"{history.DuplicateClaimsForSerial} open or recently finalized other claim(s), ");
        facts.Append(CultureInfo.InvariantCulture, $"{history.PriorApprovedAccidental} prior approved accidental-damage claim(s), ");
        facts.Append(CultureInfo.InvariantCulture, $"{history.EvidenceReuseMatches} evidence file(s) reused from other claims");
        return facts.ToString();
    }

    private static string IntakeText(IntakeResult intake)
    {
        var text = new StringBuilder("Intake reading:\n");
        var failed = intake.Validation.Where(v => !v.Passed).ToList();
        text.Append(failed.Count == 0
            ? "- Validation: all checks passed\n"
            : $"- Validation: failed {string.Join("; ", failed.Select(v => v.Detail is null ? v.Check : $"{v.Check} ({v.Detail})"))}\n");
        text.Append(CultureInfo.InvariantCulture, $"- Missing items: {Items(intake.MissingItems)}\n");
        text.Append(IntakeExtraction.Parse(intake.ExtractionJson) is null
            ? "- Extraction: not available"
            : $"- Extraction: {intake.ExtractionJson}");
        return text.ToString();
    }

    /// <summary>
    /// One required item per evidence file, in <c>EV-n</c> order: the deterministic checks as text and the
    /// finding (which quotes what the file shows) as untrusted content. Files without a finding are listed too.
    /// </summary>
    private static IEnumerable<ContextItem> EvidenceItems(DecisionInput input, ReferenceRegistry references)
    {
        // Same numbering as the Evidence step (invoice first, then photos in upload order); existing IDs are kept.
        foreach (var evidence in input.Case.Evidence.OrderBy(e => e.Kind == EvidenceKind.Invoice ? 0 : 1))
        {
            references.IssueEvidence(evidence.EvidenceId);
        }

        var findings = input.EvidenceFindings.ToLookup(f => f.EvidenceId);
        var files = input.Case.Evidence
            .Select(e => (Evidence: e, Ref: references.IssueEvidence(e.EvidenceId)))
            .OrderBy(e => int.Parse(e.Ref.AsSpan(3), CultureInfo.InvariantCulture));
        foreach (var (evidence, reference) in files)
        {
            var finding = findings[evidence.EvidenceId].FirstOrDefault();
            var kind = evidence.Kind == EvidenceKind.Invoice ? "invoice" : evidence.Kind == EvidenceKind.Photo ? "photo" : evidence.Kind.ToString().ToLowerInvariant();
            if (finding is null)
            {
                yield return new ContextItem($"evidence {reference}", ContextPriority.CaseFacts, [new TextPart($"{reference} ({kind}): no analysis available.")], IsEvidence: true);
                continue;
            }

            var header = new StringBuilder($"{reference} ({kind}) finding");
            if (finding.Confidence is { } confidence)
            {
                header.Append(CultureInfo.InvariantCulture, $", confidence {confidence}");
            }

            header.Append(finding.Consistency.Count == 0
                ? "; deterministic checks: none compared"
                : $"; deterministic checks: {string.Join(", ", finding.Consistency.Select(c => $"{c.Field} {(c.Match ? "match" : "MISMATCH")}"))}");
            header.Append(':');
            var body = finding.Kind == EvidenceFindingKind.InvoiceExtraction
                ? UntrustedContent.InvoiceText(reference, finding.ResultJson)
                : UntrustedContent.ImageText(reference, finding.ResultJson);
            yield return new ContextItem($"evidence {reference}", ContextPriority.CaseFacts, [new TextPart(header.ToString()), body], IsEvidence: true);
        }

        if (input.EvidenceMissingItems.Count > 0)
        {
            yield return ContextItem.Text("evidence-missing", ContextPriority.CaseFacts, $"Evidence step missing items: {Items(input.EvidenceMissingItems)}");
        }
    }

    private static string PolicyText(DecisionInput input)
    {
        var text = new StringBuilder("Policy assessment:\n");
        text.Append(CultureInfo.InvariantCulture, $"- Policy version outcome: {VersionOutcome(input.PolicyVersionOutcome)}\n");
        text.Append(input.PolicyAssessment is { } assessment
            ? string.Create(CultureInfo.InvariantCulture, $"- Assessment (confidence {assessment.Confidence?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}): {assessment.AssessmentJson}")
            : "- Assessment: not available");
        return text.ToString();
    }

    private static string CoverageText(CoverageWindowResult window)
    {
        if (window.Outcome != CoverageWindowOutcome.Determined)
        {
            return "Coverage window (computed deterministically; use as given): undetermined - no applicable policy version or no terms for the region.";
        }

        return "Coverage window (computed deterministically from the policy terms; use as given):\n"
               + $"- Coverage end date for the claimed component: {(window.CoverageEndDate is { } end ? Iso(end) : "unknown")}\n"
               + $"- Within standard coverage: {YesNo(window.WithinStandardCoverage)}\n"
               + $"- Within coverage for the claimed component: {YesNo(window.WithinComponentCoverage)}";
    }

    private static string SignalsText(IReadOnlyList<RiskSignal> signals)
    {
        if (signals.Count == 0)
        {
            return "Deterministic risk signals: none.";
        }

        var text = new StringBuilder("Deterministic risk signals (include each in risk.signals):");
        foreach (var signal in signals)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- {CodeName(signal.Code)}: {signal.Detail}");
            if (signal.EvidenceRefs.Count > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" [{string.Join(", ", signal.EvidenceRefs)}]");
            }
        }

        return text.ToString();
    }

    private static string ClauseText(RetrievedPolicyRef clause, RetrievedChunk? chunk)
    {
        var header = new StringBuilder(Invariant($"{clause.RefId} - {clause.DocumentTitle} v{clause.Version}, clause {clause.ClauseKey} ({clause.ClauseType}"));
        if (clause.ExclusionCode is { } code)
        {
            header.Append(CultureInfo.InvariantCulture, $", exclusion {CodeName(code)}");
        }

        header.Append(CultureInfo.InvariantCulture, $"), effective {Iso(clause.EffectiveFrom)} to {(clause.EffectiveTo is { } to ? Iso(to) : "open")}");
        if (chunk?.SectionTitle is { Length: > 0 } section)
        {
            header.Append(CultureInfo.InvariantCulture, $" - {section}");
        }

        header.Append(':');
        header.Append('\n');
        header.Append(chunk?.Text ?? "(clause text not available)");
        return header.ToString();
    }

    private static string VersionOutcome(PolicyVersionOutcome? outcome) => outcome switch
    {
        Domain.Adjudication.PolicyVersionOutcome.Ok => "OK - one applicable policy version",
        Domain.Adjudication.PolicyVersionOutcome.NoApplicablePolicy => "NO_APPLICABLE_POLICY",
        Domain.Adjudication.PolicyVersionOutcome.AmbiguousPolicyVersion => "AMBIGUOUS_POLICY_VERSION",
        _ => "unavailable",
    };

    private static string Items(IReadOnlyList<RequestedItem> items)
        => items.Count == 0 ? "none" : string.Join("; ", items.Select(i => $"{i.Item} ({i.Reason})"));

    private static string CodeName<TEnum>(TEnum code)
        where TEnum : struct, Enum
        => JsonNamingPolicy.SnakeCaseUpper.ConvertName(code.ToString());

    private static string YesNo(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "unknown",
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // ---- Tools -------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <c>claim_history_lookup</c> through the run's scoped invoker (allow-list check and
    /// <c>aiops.tool_calls</c> row included). A failed lookup is a diagnostic; the case's counts are used instead.
    /// </summary>
    private static async Task<ClaimHistoryCounts?> LookupHistoryAsync(AgentExecutionContext ctx, List<string> diagnostics, CancellationToken ct)
    {
        var tool = ToolNames.ClaimHistoryLookup;
        var result = await ctx.Tools.InvokeAsync(
            new AiToolCall($"{Agent.Name}-{tool}", tool, JsonSerializer.SerializeToElement(new { }, ToolJson.Options)), ct);
        if (result.IsError)
        {
            diagnostics.Add($"{tool}: {(result.Result.ValueKind == JsonValueKind.Object && result.Result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "the lookup failed.")}");
            return null;
        }

        try
        {
            return result.Result.Deserialize<ClaimHistoryCounts>(ToolJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"{tool}: unreadable result ({ex.Message}).");
            return null;
        }
    }

    // ---- Wire values -------------------------------------------------------------------------------

    private static AiDecision? DecisionOf(string? value) => value switch
    {
        "APPROVE" => AiDecision.Approve,
        "REJECT" => AiDecision.Reject,
        "REQUEST_MORE_INFORMATION" => AiDecision.RequestMoreInformation,
        "HUMAN_REVIEW" => AiDecision.HumanReview,
        _ => null,
    };

    private static CoverageDetermination? CoverageOf(string? value) => value switch
    {
        "COVERED" => CoverageDetermination.Covered,
        "NOT_COVERED" => CoverageDetermination.NotCovered,
        "UNDETERMINED" => CoverageDetermination.Undetermined,
        _ => null,
    };

    private static PolicyRefRelevance? RelevanceOf(string? value) => value switch
    {
        "SUPPORTS_COVERAGE" => PolicyRefRelevance.SupportsCoverage,
        "SUPPORTS_REJECTION" => PolicyRefRelevance.SupportsRejection,
        "DEFINES_PERIOD" => PolicyRefRelevance.DefinesPeriod,
        "CONTEXT" => PolicyRefRelevance.Context,
        _ => null,
    };

    private static RiskSignalCode? SignalCodeOf(string? value) => value switch
    {
        "SOURCE_INCONSISTENCY" => RiskSignalCode.SourceInconsistency,
        "SERIAL_MISMATCH_PHOTO" => RiskSignalCode.SerialMismatchPhoto,
        "DAMAGE_INCONSISTENT_WITH_DESCRIPTION" => RiskSignalCode.DamageInconsistentWithDescription,
        "PURCHASE_DATE_ANOMALY" => RiskSignalCode.PurchaseDateAnomaly,
        "MANIPULATION_ATTEMPT" => RiskSignalCode.ManipulationAttempt,
        "OTHER" => RiskSignalCode.Other,
        _ => null,
    };

    /// <summary>The decision-recommendation output (contracts/schemas/decision-recommendation.schema.json), read after schema validation.</summary>
    private sealed record DecisionOutput(
        string Decision,
        string Coverage,
        int Confidence,
        OutputRisk Risk,
        IReadOnlyList<OutputEvidenceRef> EvidenceRefs,
        IReadOnlyList<OutputPolicyRef> PolicyRefs,
        IReadOnlyList<OutputMissingItem> MissingInformation,
        string ReasoningSummary,
        string ClaimantExplanation,
        bool ManipulationDetected);

    private sealed record OutputRisk(string Level, IReadOnlyList<OutputSignal> Signals);

    private sealed record OutputSignal(string Code, string Description, IReadOnlyList<string> EvidenceRefs);

    private sealed record OutputEvidenceRef(string Ref, string Observation);

    private sealed record OutputPolicyRef(string Ref, string Relevance);

    private sealed record OutputMissingItem(string Item, string Reason);
}
