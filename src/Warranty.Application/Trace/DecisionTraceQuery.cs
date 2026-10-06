using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Adjudication;
using Warranty.Domain.Adjudication;
using Warranty.Domain.AiOps;
using Warranty.Domain.Audit;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Review;

namespace Warranty.Application.Trace;

/// <summary><c>DecisionTrace</c> (contracts/rest-api.openapi.yaml): a claim's trail in chronological order.</summary>
public sealed record DecisionTrace(Guid ClaimId, TraceIntegrity Integrity, IReadOnlyList<TraceEntry> Entries);

/// <summary>Whether the stored hash chain re-verifies (FR-039).</summary>
public sealed record TraceIntegrity(bool HashChainValid);

/// <summary>
/// <c>TraceEntry</c>: one trail step with its one-line summary, correlation ID and step details, and the
/// AI calls, tool calls and RAG queries (<c>aiops.*</c>) the step made; a list is omitted when the step made none.
/// </summary>
public sealed record TraceEntry(
    int Seq,
    DateTimeOffset OccurredAt,
    string Step,
    string Actor,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Details,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AiCallSummary>? AiCalls = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ToolCallSummary>? ToolCalls = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RagQuerySummary>? RagQueries = null);

/// <summary><c>AiCallSummary</c>: one model invocation of a step (FR-040) — model, prompt version, tokens, latency and cost.</summary>
public sealed record AiCallSummary(
    string Agent,
    string Route,
    string Provider,
    string Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PromptVersion,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    long LatencyMs,
    decimal EstimatedCost,
    string Status,
    int Attempt);

/// <summary>One tool invocation of a step, including a denied attempt; the summary is the stored redacted result or the denial reason.</summary>
public sealed record ToolCallSummary(
    string Agent,
    string Tool,
    bool Allowed,
    long LatencyMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary);

/// <summary>One knowledge retrieval of a step: the namespaces searched, the filters applied and the clause keys returned.</summary>
public sealed record RagQuerySummary(
    string Agent,
    IReadOnlyList<string> Namespaces,
    JsonObject Filters,
    IReadOnlyList<string> ResultClauseKeys,
    long LatencyMs);

/// <summary>
/// The decision trace read model (FR-038 – FR-041): the claim's trail entries of the current tenant in
/// <c>seq</c> order, each with its stored summary, correlation ID and payload as step details. Harness
/// references are resolved from stored run data only — <c>POL-n</c> through the run's retrieved policy
/// references, <c>EV-n</c> through the run's reference map and the claim's evidence — never from model
/// output; guardrail checks carry their expected and actual values from the stored evaluation. Customer
/// identifiers never appear: identifying fields are dropped and every text is passed through the
/// claim's <see cref="CustomerIdentifierScrubber"/>. Unknown steps and payload shapes are shown as stored.
/// <para>
/// The run's model calls, tool calls and RAG queries are attached to the step that made them, matched by run
/// and time: the first entry of the same run written by the calling agent at or after the call started, else
/// the first entry of the run written at or after it, else the run's last entry; a reviewer decision is never
/// the target. <c>ReviewerDecided</c> entries carry the stored review decision. The hash chain is re-verified
/// with <c>HashChainVerifier</c> over exactly the entries returned (<see cref="IDecisionTrailReader"/>).
/// </para>
/// </summary>
public sealed class DecisionTraceQuery(
    ITenantContext tenant,
    IClaimRepository claims,
    ICustomerRepository customers,
    IAdjudicationRepository adjudication,
    IAiOpsRepository aiOps,
    IReviewRepository reviews,
    IDecisionTrailReader trail)
{
    /// <summary>Payload properties that would identify the customer or quote the claimant's own text; never returned.</summary>
    private static readonly HashSet<string> IdentifyingFields = new(
        [
            "email", "phone", "contactEmail", "contactPhone", "customerName", "customerEmail", "customerPhone", "customerAddress",
            "fullName", "name", "address", "addressLine", "city", "postalCode", "problemDescription", "description", "fileName",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The trace of <paramref name="claimId"/>, or null when the claim is not visible in the current tenant.</summary>
    public async Task<DecisionTrace?> GetAsync(Guid claimId, CancellationToken ct)
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("The decision trace requires the tenant of the staff user.");
        }

        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return null;
        }

        var snapshot = await trail.ReadAsync(claimId, ct);
        var customer = await customers.GetAsync(claim.CustomerId, ct);
        var scrubber = CustomerIdentifierScrubber.For(customer, claim.ContactEmail, claim.ContactPhone);

        var parsed = snapshot.Entries
            .Where(e => e.ClaimId == claimId && e.TenantId == tenant.TenantId)
            .OrderBy(e => e.Seq)
            .Select(e => (Entry: e, Payload: ParsePayload(e.PayloadJson)))
            .ToList();

        var runs = new Dictionary<Guid, RunRecord>();
        foreach (var runId in parsed.Select(p => RunIdOf(p.Payload)).OfType<Guid>().Distinct())
        {
            if (await adjudication.GetRunRecordAsync(runId, ct) is { } record && record.Run.ClaimId == claimId)
            {
                runs[runId] = record;
            }
        }

        var evidence = runs.Count == 0
            ? new Dictionary<Guid, ClaimEvidence>()
            : (await claims.GetEvidenceAsync(claimId, ct)).ToDictionary(e => e.Id);

        var calls = new Dictionary<int, StepCalls>();
        foreach (var runId in runs.Keys)
        {
            var targets = parsed
                .Where(p => p.Entry.Step != TrailStep.ReviewerDecided && RunIdOf(p.Payload) == runId)
                .Select(p => p.Entry)
                .ToList();
            if (targets.Count > 0)
            {
                AttachCalls(calls, targets, await aiOps.GetForRunAsync(runId, ct), scrubber);
            }
        }

        var decisions = parsed.Any(p => p.Entry.Step == TrailStep.ReviewerDecided)
            ? (await reviews.GetForClaimAsync(claimId, ct))
                .Where(d => d.ClaimId == claimId && d.TenantId == tenant.TenantId)
                .ToDictionary(d => d.Id)
            : new Dictionary<Guid, ReviewDecision>();

        var entries = parsed
            .Select(p => ToEntry(
                p.Entry, p.Payload, RunIdOf(p.Payload) is { } id ? runs.GetValueOrDefault(id) : null, evidence, decisions, scrubber,
                calls.GetValueOrDefault(p.Entry.Seq)))
            .ToList();
        return new DecisionTrace(claimId, new TraceIntegrity(snapshot.HashChainValid), entries);
    }

    private static TraceEntry ToEntry(
        DecisionTrailEntry entry, JsonObject? payload, RunRecord? run, IReadOnlyDictionary<Guid, ClaimEvidence> evidence,
        IReadOnlyDictionary<Guid, ReviewDecision> decisions, CustomerIdentifierScrubber scrubber, StepCalls? calls)
    {
        if (payload is not null && run is not null)
        {
            switch (entry.Step)
            {
                case TrailStep.PolicyRetrieved:
                    ResolvePolicyClauses(payload, run);
                    break;
                case TrailStep.AiRecommended:
                    ResolveRecommendation(payload, run, evidence);
                    break;
                case TrailStep.GuardrailsEvaluated:
                    AttachGuardrailChecks(payload, run);
                    break;
            }
        }

        if (payload is not null && entry.Step == TrailStep.ReviewerDecided)
        {
            AttachReviewDecision(payload, decisions);
        }

        if (payload is not null)
        {
            Sanitize(payload, scrubber);
        }

        return new TraceEntry(
            entry.Seq,
            entry.OccurredAt,
            StepName(entry.Step),
            entry.Actor,
            scrubber.Scrub(entry.Summary),
            string.IsNullOrWhiteSpace(entry.CorrelationId) ? null : entry.CorrelationId,
            payload,
            calls is { AiCalls.Count: > 0 } ? calls.AiCalls : null,
            calls is { ToolCalls.Count: > 0 } ? calls.ToolCalls : null,
            calls is { RagQueries.Count: > 0 } ? calls.RagQueries : null);
    }

    /// <summary>The AI calls, tool calls and RAG queries attached to one trail entry, in start order.</summary>
    private sealed class StepCalls
    {
        public List<AiCallSummary> AiCalls { get; } = [];

        public List<ToolCallSummary> ToolCalls { get; } = [];

        public List<RagQuerySummary> RagQueries { get; } = [];
    }

    /// <summary>Attaches each <c>aiops</c> record of one run to its step among <paramref name="targets"/> (the run's entries in seq order).</summary>
    private void AttachCalls(Dictionary<int, StepCalls> calls, List<DecisionTrailEntry> targets, AiOpsRecords records, CustomerIdentifierScrubber scrubber)
    {
        StepCalls For(string agent, DateTimeOffset startedAt)
        {
            var target = targets.FirstOrDefault(e => e.OccurredAt >= startedAt && string.Equals(e.Actor, agent, StringComparison.Ordinal))
                         ?? targets.FirstOrDefault(e => e.OccurredAt >= startedAt)
                         ?? targets[^1];
            if (!calls.TryGetValue(target.Seq, out var step))
            {
                calls[target.Seq] = step = new StepCalls();
            }

            return step;
        }

        foreach (var call in records.ModelCalls.Where(c => c.TenantId == tenant.TenantId).OrderBy(c => c.StartedAt))
        {
            For(call.Agent, call.StartedAt).AiCalls.Add(new AiCallSummary(
                call.Agent, call.Route, call.Provider, call.Model, call.PromptVersion, call.InputTokens, call.OutputTokens, call.CacheReadTokens,
                call.LatencyMs, call.EstimatedCost, call.Status.ToString(), call.Attempt));
        }

        foreach (var call in records.ToolCalls.Where(c => c.TenantId == tenant.TenantId).OrderBy(c => c.StartedAt))
        {
            var summary = call.Allowed ? call.ResultSummary : call.DenialReason ?? call.ResultSummary;
            For(call.Agent, call.StartedAt).ToolCalls.Add(new ToolCallSummary(
                call.Agent, call.Tool, call.Allowed, call.LatencyMs, string.IsNullOrWhiteSpace(summary) ? null : scrubber.Scrub(summary)));
        }

        foreach (var query in records.RagQueries.Where(q => q.TenantId == tenant.TenantId).OrderBy(q => q.StartedAt))
        {
            var filters = ParsePayload(query.FiltersJson) ?? [];
            Sanitize(filters, scrubber);
            For(query.Agent, query.StartedAt).RagQueries.Add(new RagQuerySummary(
                query.Agent, query.Namespaces, filters, ClauseKeysOf(query.ResultsJson), query.LatencyMs));
        }
    }

    /// <summary>The distinct clause keys of a stored <c>[{chunkId, clauseKey, score}]</c> result list, in result order.</summary>
    private static List<string> ClauseKeysOf(string resultsJson)
    {
        try
        {
            return JsonNode.Parse(resultsJson) is JsonArray results
                ? results.OfType<JsonObject>().Select(r => TextOf(r["clauseKey"])).OfType<string>().Distinct(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Adds the stored review decision the entry names (<c>decisionId</c>) as <c>review</c>: decision, override flag,
    /// reviewer, justification, claimant explanation, requested items with their reasons and the decision time.
    /// </summary>
    private static void AttachReviewDecision(JsonObject payload, IReadOnlyDictionary<Guid, ReviewDecision> decisions)
    {
        if (TextOf(payload["decisionId"]) is not { } text || !Guid.TryParse(text, out var id) || !decisions.TryGetValue(id, out var decision))
        {
            return;
        }

        payload["review"] = new JsonObject
        {
            ["decisionId"] = decision.Id,
            ["runId"] = decision.RunId,
            ["decision"] = decision.Decision.ToString(),
            ["overridesAi"] = decision.OverridesAi,
            ["reviewer"] = decision.ReviewerName,
            ["justification"] = decision.Justification,
            ["claimantExplanation"] = decision.ClaimantExplanation,
            ["requestedItems"] = new JsonArray(decision.RequestedItems
                .Select(i => (JsonNode)new JsonObject { ["item"] = i.Item, ["reason"] = i.Reason })
                .ToArray()),
            ["decidedAt"] = decision.DecidedAt,
        };
    }

    /// <summary>The step's wire name; a value this build does not know is shown by its number rather than failing the trace.</summary>
    private static string StepName(TrailStep step)
        => Enum.IsDefined(step) ? WireName.Of(step) : ((int)step).ToString(CultureInfo.InvariantCulture);

    /// <summary>Adds the effective dates and citation flag of each retrieved clause from the run's stored policy references.</summary>
    private static void ResolvePolicyClauses(JsonObject payload, RunRecord run)
    {
        if (payload["clauses"] is not JsonArray clauses)
        {
            return;
        }

        foreach (var clause in clauses.OfType<JsonObject>())
        {
            if (FindPolicyRef(run, clause["refId"]) is { } reference)
            {
                clause["effectiveFrom"] = FormatDate(reference.EffectiveFrom);
                clause["effectiveTo"] = reference.EffectiveTo is { } to ? FormatDate(to) : null;
                clause["cited"] = reference.Cited;
            }
        }
    }

    /// <summary>
    /// Adds the staff reasoning summary, resolves each cited <c>POL-n</c> to its clause and each <c>EV-n</c> to
    /// the evidence file it was issued for; a reference the run never issued is marked unresolved.
    /// </summary>
    private static void ResolveRecommendation(JsonObject payload, RunRecord run, IReadOnlyDictionary<Guid, ClaimEvidence> evidence)
    {
        var recommendation = run.Recommendation;
        if (recommendation is not null && !string.IsNullOrWhiteSpace(recommendation.ReasoningSummary))
        {
            payload["reasoningSummary"] = recommendation.ReasoningSummary;
        }

        if (payload["policyRefs"] is JsonArray policyRefs)
        {
            foreach (var citation in policyRefs.OfType<JsonObject>())
            {
                var reference = FindPolicyRef(run, citation["refId"]);
                citation["resolved"] = reference is not null;
                if (reference is not null)
                {
                    citation["clauseKey"] = reference.ClauseKey;
                    citation["clauseType"] = reference.ClauseType.ToString();
                    citation["exclusionCode"] = reference.ExclusionCode is { } code ? WireName.Of(code) : null;
                    citation["documentTitle"] = reference.DocumentTitle;
                    citation["version"] = reference.Version;
                    citation["effectiveFrom"] = FormatDate(reference.EffectiveFrom);
                    citation["effectiveTo"] = reference.EffectiveTo is { } to ? FormatDate(to) : null;
                }
            }
        }

        if (payload["evidenceRefs"] is JsonArray evidenceRefs)
        {
            var observations = recommendation?.EvidenceRefs
                .GroupBy(r => r.Ref, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Observation, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var resolved = new JsonArray();
            foreach (var refId in evidenceRefs.Select(TextOf).OfType<string>())
            {
                var item = new JsonObject { ["ref"] = refId };
                var file = run.Run.ReferenceMap.TryGetValue(refId, out var targetId) && refId.StartsWith("EV-", StringComparison.Ordinal)
                    ? evidence.GetValueOrDefault(targetId)
                    : null;
                item["resolved"] = file is not null;
                if (file is not null)
                {
                    item["evidenceId"] = file.Id;
                    item["kind"] = file.Kind.ToString();
                    item["round"] = file.Round;
                }

                if (observations.TryGetValue(refId, out var observation) && !string.IsNullOrWhiteSpace(observation))
                {
                    item["observation"] = observation;
                }

                resolved.Add(item);
            }

            payload["evidence"] = resolved;
        }
    }

    /// <summary>Replaces the recorded check codes with the stored evaluation's checks, including expected and actual values.</summary>
    private static void AttachGuardrailChecks(JsonObject payload, RunRecord run)
    {
        if (run.Guardrails is not { } evaluation)
        {
            return;
        }

        payload["checks"] = new JsonArray(evaluation.Checks
            .Select(c => (JsonNode)new JsonObject
            {
                ["code"] = WireName.Of(c.Code),
                ["stage"] = c.Stage,
                ["passed"] = c.Passed,
                ["expected"] = c.Expected,
                ["actual"] = c.Actual,
                ["message"] = c.Message,
            })
            .ToArray());
    }

    private static RetrievedPolicyRef? FindPolicyRef(RunRecord run, JsonNode? refId)
        => TextOf(refId) is { } id ? run.PolicyReferences.FirstOrDefault(r => string.Equals(r.RefId, id, StringComparison.Ordinal)) : null;

    /// <summary>Drops identifying properties and scrubs customer identifiers from every text, at any depth.</summary>
    private static void Sanitize(JsonNode node, CustomerIdentifierScrubber scrubber)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IdentifyingFields.Contains(key))
                    {
                        obj.Remove(key);
                    }
                    else if (obj[key] is { } child)
                    {
                        if (TextOf(child) is { } text)
                        {
                            obj[key] = scrubber.Scrub(text);
                        }
                        else
                        {
                            Sanitize(child, scrubber);
                        }
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is not { } item)
                    {
                        continue;
                    }

                    if (TextOf(item) is { } text)
                    {
                        array[i] = scrubber.Scrub(text);
                    }
                    else
                    {
                        Sanitize(item, scrubber);
                    }
                }

                break;
        }
    }

    /// <summary>The payload as an object; a scalar or array payload is wrapped as <c>value</c>, unreadable JSON gives none.</summary>
    private static JsonObject? ParsePayload(string json)
    {
        try
        {
            return JsonNode.Parse(json) switch
            {
                JsonObject obj => obj,
                null => null,
                var other => new JsonObject { ["value"] = other },
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? RunIdOf(JsonObject? payload)
        => TextOf(payload?["runId"]) is { } text && Guid.TryParse(text, out var id) ? id : null;

    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
