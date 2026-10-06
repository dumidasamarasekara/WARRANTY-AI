using System.Globalization;
using System.Text;
using System.Text.Json;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Agents;

/// <summary>The Policy agent's input: the case and the run's intake result (contracts/agents-and-tools.md "Case + intake").</summary>
public sealed record PolicyInput(CaseContext Case, IntakeResult Intake);

/// <summary>
/// The Policy Agent (contracts/agents-and-tools.md, contracts/rag.md). The deterministic part retrieves the
/// applicable version's clauses with <see cref="IKnowledgeRetriever.RetrievePolicyClausesAsync"/>: the query
/// text is built from the intake extraction (component, problem, symptoms, summary), never from the raw
/// claimant description, and the filters are those of <c>search_policy_knowledge</c> and
/// <c>warranty_lookup</c> (<see cref="SearchPolicyKnowledgeTool.ApplicabilityFor"/>). The clauses are issued
/// as <c>POL-n</c> — <c>Period</c> and <c>Exclusion</c> clauses first in clause-key order, then <c>Coverage</c>
/// clauses in clause-key order, then the others by score (<see cref="IssueOrder"/>) — and the coverage window is computed with <see cref="CoverageWindowCalculator"/>. The model part
/// runs on the <c>policy-reasoning</c> route with <c>warranty_lookup</c>, <c>search_policy_knowledge</c> and
/// <c>search_global_knowledge</c> and returns a <c>policy-assessment</c>.
/// </summary>
/// <remarks>
/// <para>
/// When no policy version applies, or more than one does (FR-014), or retrieval hit a scope violation,
/// there is nothing to interpret: the model is not called, the outcome is stored with an empty assessment
/// (<c>{}</c>) and the guardrails route the claim to review. No/ambiguous policy is a deterministic outcome,
/// so the agent succeeds; a scope violation is a failed step.
/// </para>
/// <para>
/// One <c>adjudication.policy_assessments</c> row and one <c>adjudication.retrieved_policy_refs</c> row per
/// issued <c>POL-n</c> (including clauses the model found with <c>search_policy_knowledge</c>) are added to
/// the run's unit of work; the caller commits them. When the model step fails, the result keeps the
/// clauses, version and window, stores <c>{}</c> as assessment and comes back as the output of the failed
/// <see cref="AgentResult{T}"/>. An assessment citing a reference that was not issued as <c>POL-n</c> is
/// invalid output.
/// </para>
/// </remarks>
public sealed class PolicyAgent(
    AgentTurnLoop loop,
    ContextBuilder contextBuilder,
    SchemaValidator schemas,
    IKnowledgeRetriever knowledge,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IPolicyRepository policies,
    IAdjudicationRepository adjudication) : IAgent<PolicyInput, PolicyResult>
{
    public const string Route = "policy-reasoning";

    /// <summary>The assessment stored when the model step produced none (or was not run).</summary>
    public const string NoAssessment = "{}";

    /// <summary>Top-k of the run's policy retrieval; the version's Period and Exclusion clauses come on top.</summary>
    public const int RetrievalTopK = 8;

    /// <summary>The Policy Agent's descriptor (budgets: contracts/agents-and-tools.md).</summary>
    public static AgentDescriptor Agent { get; } = new(
        AgentNames.Policy,
        Route,
        new PromptRef("policy", 1),
        "warranty-ai/policy-assessment/v1",
        [ToolNames.WarrantyLookup, ToolNames.SearchPolicyKnowledge, ToolNames.SearchGlobalKnowledge],
        MaxTurns: 6,
        InputTokenBudget: 16_000);

    /// <summary>The only variable of the <c>policy</c> prompt template.</summary>
    private static readonly IReadOnlyDictionary<string, string> PromptVariables =
        new Dictionary<string, string>(StringComparer.Ordinal) { [UntrustedContent.PreambleVariable] = UntrustedContent.Preamble };

    /// <summary>Label of the intake's free-text reading, which was derived from claimant text.</summary>
    public const string IntakeSummaryLabel = "intake_summary";

    public AgentDescriptor Descriptor => Agent;

    public async Task<AgentResult<PolicyResult>> RunAsync(PolicyInput input, AgentExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(input.Case);
        ArgumentNullException.ThrowIfNull(input.Intake);
        if (input.Case.ClaimId != ctx.Run.ClaimId)
        {
            throw new ArgumentException("The case is not the run's claim.", nameof(input));
        }

        if (input.Intake.RunId != ctx.Run.RunId)
        {
            throw new ArgumentException("The intake result is not the run's.", nameof(input));
        }

        using var span = ctx.Trace.StartSpan($"agent.{Agent.Name}", new Dictionary<string, object?> { ["agent"] = Agent.Name, ["claim_id"] = input.Case.ClaimId });
        var diagnostics = new List<string>();

        var extraction = IntakeExtraction.From(input.Intake);
        if (extraction is null)
        {
            diagnostics.Add("intake: no extraction; the retrieval query uses the product only and the component is UNKNOWN.");
        }

        var claim = await ToolSupport.RequireClaimAsync(claims, ctx.Run.ClaimId, ct);
        // The case's product is set only when the claimed serial is registered to it; otherwise the claim is
        // not in the tenant's catalog and only catalog-independent documents apply (FR-003).
        var product = input.Case.Product is { } known ? await catalog.GetProductAsync(known.ProductId, ct) : null;
        var applicability = SearchPolicyKnowledgeTool.ApplicabilityFor(claim, product);
        var component = ComponentOf(extraction);

        var retrieval = await RetrieveAsync(applicability, BuildQuery(extraction, product, input.Case), ctx, diagnostics, ct);
        if (retrieval.Outcome != RetrievalOutcome.Ok || retrieval.Version is null)
        {
            return WithoutPolicy(retrieval.Outcome, component, ctx, diagnostics);
        }

        var version = retrieval.Version;
        var issued = Issue(retrieval.Chunks, ctx.Run.References);
        var window = CoverageWindowCalculator.Calculate(
            version.Terms, applicability.Region!.Value, WarrantyLookupTool.ComponentKey(component), claim.PurchaseDate, claim.ClaimDate);

        var reasoning = await ReasonAsync(input.Case, extraction, product, issued, ctx, ct);
        diagnostics.AddRange(reasoning.Diagnostics);

        var clauses = Record(issued, ctx, version, diagnostics);
        var assessment = PolicyAssessment.Create(
            ctx.Run.RunId,
            ctx.Run.Tenant.TenantId,
            PolicyVersionOutcome.Ok,
            reasoning.Json ?? NoAssessment,
            reasoning.Confidence,
            reasoning.Model,
            reasoning.Json is null ? string.Empty : Agent.Prompt.ToString());
        adjudication.AddPolicyAssessment(assessment);

        var result = new PolicyResult(RetrievalOutcome.Ok, clauses, assessment, version)
        {
            CoverageWindow = window,
            Component = component,
            CoverageAssessment = reasoning.CoverageAssessment,
            IsAmbiguous = reasoning.IsAmbiguous,
            AmbiguityExplanation = reasoning.AmbiguityExplanation,
        };

        return reasoning.Status == AgentStatus.Succeeded
            ? AgentResult<PolicyResult>.Success(result, [.. diagnostics])
            : new AgentResult<PolicyResult>(reasoning.Status, result, diagnostics);
    }

    /// <summary>
    /// The retrieval query: the extraction's component, problem category, symptoms and summary — the
    /// structured reading of the problem, never the claimant's own words — plus the product category.
    /// </summary>
    public static string BuildQuery(IntakeExtraction? extraction, Product? product, CaseContext @case)
    {
        ArgumentNullException.ThrowIfNull(@case);
        var category = product?.Category ?? @case.Product?.Category;
        var subject = string.IsNullOrWhiteSpace(category) ? "product" : category;
        if (extraction is null)
        {
            return $"Warranty coverage, warranty period and exclusions for a {subject} defect.";
        }

        var query = new StringBuilder();
        query.Append(CultureInfo.InvariantCulture, $"{Humanize(extraction.ProblemCategory)} of the {Humanize(extraction.Component)} of a {subject}");
        if (extraction.Symptoms.Count > 0)
        {
            query.Append(": ").Append(string.Join("; ", extraction.Symptoms));
        }

        query.Append(". ").Append(extraction.Summary);
        return query.ToString().Trim();
    }

    /// <summary>
    /// The order <c>POL-n</c> are issued in: <c>Period</c> and <c>Exclusion</c> clauses first in clause-key
    /// order (natural: <c>2.9</c> before <c>2.10</c>), then <c>Coverage</c> clauses in clause-key order, then the
    /// other clauses by descending score. Ties fall back to clause key and chunk ID, so the numbering is
    /// deterministic (replay fixtures depend on it). The clauses a decision is grounded in — the period, the
    /// exclusions and the coverage grant an approval cites — never depend on similarity scores, which differ
    /// between embedding models (Ollama in the app, hash embeddings in the integration tests).
    /// </summary>
    public static IReadOnlyList<RetrievedChunk> IssueOrder(IEnumerable<RetrievedChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var distinct = chunks.DistinctBy(c => c.ChunkId).ToList();
        var decisive = distinct.Where(IsDecisive)
            .OrderBy(c => c.ClauseKey, ClauseKeyComparer.Instance)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.ChunkId);
        var coverage = distinct.Where(c => c.ClauseType == ClauseType.Coverage)
            .OrderBy(c => c.ClauseKey, ClauseKeyComparer.Instance)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.ChunkId);
        var others = distinct.Where(c => !IsDecisive(c) && c.ClauseType != ClauseType.Coverage)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.ClauseKey, ClauseKeyComparer.Instance)
            .ThenBy(c => c.ChunkId);
        return [.. decisive, .. coverage, .. others];
    }

    /// <summary>The intake component (upper-case wire value) the coverage window is computed for; <c>UNKNOWN</c> without an extraction.</summary>
    internal static string ComponentOf(IntakeExtraction? extraction)
        => string.IsNullOrWhiteSpace(extraction?.Component) ? "UNKNOWN" : extraction.Component.Trim().ToUpperInvariant();

    private static bool IsDecisive(RetrievedChunk chunk) => chunk.ClauseType is ClauseType.Period or ClauseType.Exclusion;

    /// <summary>
    /// Runs the version-correct retrieval and selects the version: exactly one policy version must stand
    /// behind the recordable clauses. Without a determinable region nothing can apply (as in <c>warranty_lookup</c>).
    /// </summary>
    private async Task<Retrieval> RetrieveAsync(
        PolicyApplicability applicability, string query, AgentExecutionContext ctx, List<string> diagnostics, CancellationToken ct)
    {
        if (applicability.Region is not { } region || applicability.PurchaseDate is not { } purchaseDate)
        {
            diagnostics.Add("retrieval: the claim's region is not determined, so no policy version applies.");
            return new Retrieval(RetrievalOutcome.NoApplicablePolicy, [], null);
        }

        var result = await knowledge.RetrievePolicyClausesAsync(
            new PolicyRetrievalQuery(
                query,
                applicability.ProductCategory ?? string.Empty,
                applicability.ProductModel,
                region,
                purchaseDate,
                RetrievalTopK,
                new RetrievalAttribution(Agent.Name, ctx.Run.RunId, ctx.Run.ClaimId)),
            ct);
        if (result.Outcome != RetrievalOutcome.Ok)
        {
            diagnostics.Add($"retrieval: {result.Outcome}.");
            return new Retrieval(result.Outcome, [], null);
        }

        var recordable = result.Chunks.Where(c => IsRecordable(c, null)).ToList();
        var dropped = result.Chunks.Count - recordable.Count;
        if (dropped > 0)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"retrieval: {dropped} chunk(s) without policy version, clause key or clause type were left out."));
        }

        var versionIds = recordable.Select(c => c.PolicyVersionId!.Value).Distinct().ToList();
        switch (versionIds.Count)
        {
            case 0:
                diagnostics.Add("retrieval: no clause of an applicable policy version was found.");
                return new Retrieval(RetrievalOutcome.NoApplicablePolicy, [], null);
            case > 1:
                diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"retrieval: the clauses belong to {versionIds.Count} policy versions."));
                return new Retrieval(RetrievalOutcome.AmbiguousPolicyVersion, [], null);
        }

        var version = await policies.GetVersionAsync(versionIds[0], ct);
        if (version is null)
        {
            diagnostics.Add("retrieval: the clauses' policy version was not found.");
            return new Retrieval(RetrievalOutcome.NoApplicablePolicy, [], null);
        }

        return new Retrieval(RetrievalOutcome.Ok, recordable, version);
    }

    /// <summary>No/ambiguous policy or a scope violation: the outcome is stored without a model call.</summary>
    private AgentResult<PolicyResult> WithoutPolicy(RetrievalOutcome outcome, string component, AgentExecutionContext ctx, List<string> diagnostics)
    {
        var result = new PolicyResult(outcome, [], null, null) { Component = component };
        var assessment = PolicyAssessment.Create(
            ctx.Run.RunId, ctx.Run.Tenant.TenantId, result.VersionOutcome, NoAssessment, null, string.Empty, string.Empty);
        adjudication.AddPolicyAssessment(assessment);
        result = result with { Assessment = assessment };

        return outcome == RetrievalOutcome.ScopeViolation
            ? new AgentResult<PolicyResult>(AgentStatus.Failed, result, [.. diagnostics, "retrieval_scope_violation: the policy retrieval was refused."])
            : AgentResult<PolicyResult>.Success(result, [.. diagnostics]);
    }

    /// <summary>Issues the clauses' <c>POL-n</c> in <see cref="IssueOrder"/> through the run's registry.</summary>
    private static IReadOnlyList<IssuedClause> Issue(IReadOnlyList<RetrievedChunk> chunks, ReferenceRegistry references)
        => IssueOrder(chunks).Select(c => new IssuedClause(references.IssueChunk(c), c)).ToList();

    /// <summary>
    /// The run's <c>retrieved_policy_refs</c>: the retrieved clauses in issue order, then every other
    /// <c>POL-n</c> the run issued (clauses found by <c>search_policy_knowledge</c>) in number order.
    /// </summary>
    private IReadOnlyList<RetrievedPolicyRef> Record(
        IReadOnlyList<IssuedClause> retrieved, AgentExecutionContext ctx, PolicyVersion version, List<string> diagnostics)
    {
        var known = retrieved.Select(c => c.Ref).ToHashSet(StringComparer.Ordinal);
        var searched = ctx.Run.References.Entries
            .Where(e => e.Kind == ReferenceKind.Policy && e.Chunk is not null && !known.Contains(e.Id))
            .Select(e => new IssuedClause(e.Id, e.Chunk!));

        var clauses = new List<RetrievedPolicyRef>();
        foreach (var (refId, chunk) in retrieved.Concat(searched))
        {
            if (!IsRecordable(chunk, version))
            {
                diagnostics.Add($"{refId}: the clause has no policy version, clause key, clause type or effective date and is not citable.");
                continue;
            }

            var reference = RetrievedPolicyRef.Create(
                Guid.CreateVersion7(),
                ctx.Run.Tenant.TenantId,
                ctx.Run.RunId,
                refId,
                chunk.ChunkId,
                chunk.PolicyVersionId!.Value,
                chunk.ClauseKey!,
                chunk.ClauseType!.Value,
                chunk.ExclusionCode,
                chunk.DocumentTitle,
                chunk.Version,
                EffectiveFrom(chunk, version)!.Value,
                chunk.EffectiveFrom is null ? version.EffectiveTo : chunk.EffectiveTo,
                (float)chunk.Score);
            adjudication.AddRetrievedPolicyRef(reference);
            clauses.Add(reference);
        }

        return clauses;
    }

    private static bool IsRecordable(RetrievedChunk chunk, PolicyVersion? version)
        => chunk.PolicyVersionId is { } id && id != Guid.Empty
           && chunk.ClauseType is not null
           && !string.IsNullOrWhiteSpace(chunk.ClauseKey)
           && !string.IsNullOrWhiteSpace(chunk.DocumentTitle)
           && (version is null || EffectiveFrom(chunk, version) is not null);

    /// <summary>The chunk's effective date, else its version's when it is the applicable one.</summary>
    private static DateOnly? EffectiveFrom(RetrievedChunk chunk, PolicyVersion version)
        => chunk.EffectiveFrom ?? (chunk.PolicyVersionId == version.Id ? version.EffectiveFrom : null);

    private async Task<Reasoning> ReasonAsync(
        CaseContext @case, IntakeExtraction? extraction, Product? product, IReadOnlyList<IssuedClause> clauses, AgentExecutionContext ctx, CancellationToken ct)
    {
        var items = new List<ContextItem>
        {
            ContextItem.Text(
                "instructions",
                ContextPriority.Instructions,
                "Decide which of the policy clauses below apply to this claim and what they mean for coverage. Call warranty_lookup "
                + "with the component from the intake reading for the coverage window; never compute dates yourself. Cite only the POL-n "
                + "IDs shown here or returned by search_policy_knowledge. Return only the policy assessment."),
            ContextItem.Text("case-facts", ContextPriority.CaseFacts, CaseFacts(@case, product)),
            new ContextItem("intake-reading", ContextPriority.CaseFacts, IntakeReading(extraction)),
        };
        items.AddRange(clauses.Select(c => ContextItem.Text(
            $"clause {c.Ref}",
            IsDecisive(c.Chunk) ? ContextPriority.DecisiveClauses : ContextPriority.OtherClauses,
            Render(c),
            c.Chunk.Score)));

        var context = contextBuilder.Build(items, Agent.InputTokenBudget);
        if (context.IsOverflow)
        {
            return Reasoning.Failed(
                AgentStatus.Failed,
                string.Create(CultureInfo.InvariantCulture, $"context_overflow: the case facts and intake reading need ~{context.EstimatedTokens} tokens; the budget is {context.Budget}."));
        }

        string[] diagnostics = context.Dropped.Count > 0 ? [$"context: dropped {string.Join(", ", context.Dropped)} to fit the budget."] : [];
        var conversation = new AiConversation();
        conversation.AddUser([.. context.Parts]);
        var outcome = await loop.RunAsync(
            new AgentTurnLoopRequest(Agent, conversation, PromptVariables, schemas.GetOutputSchema(Agent.OutputSchemaId!), AgentTurnLoopRequest.DefaultTurnTimeout),
            ctx,
            ct);
        var model = outcome.LastTurn.Usage.Model;

        if (outcome.Status != AgentStatus.Succeeded)
        {
            var failure = outcome.LastTurn.Failure;
            var message = $"{outcome.Outcome}: {failure?.Message ?? $"the model stopped with '{outcome.LastTurn.Stop}'."}";
            return Reasoning.Failed(outcome.Status, [.. diagnostics, message, .. failure?.ValidationErrors ?? []]) with { Model = model };
        }

        if (outcome.LastTurn.StructuredOutput is not { } output)
        {
            return Reasoning.Failed(AgentStatus.InvalidOutput, [.. diagnostics, "The model returned no structured output."]) with { Model = model };
        }

        var validation = schemas.Validate(Agent.OutputSchemaId!, output);
        var errors = validation.IsValid ? ReferenceErrors(output, ctx.Run.References) : validation.Errors;
        if (errors.Count > 0)
        {
            return Reasoning.Failed(AgentStatus.InvalidOutput, [.. diagnostics, .. errors]) with { Model = model };
        }

        var ambiguity = output.GetProperty("ambiguity");
        return new Reasoning(
            AgentStatus.Succeeded,
            output.GetRawText(),
            Confidence(output.GetProperty("confidence")),
            model,
            output.GetProperty("coverageAssessment").GetString(),
            ambiguity.GetProperty("isAmbiguous").GetBoolean(),
            ambiguity.GetProperty("explanation").GetString(),
            diagnostics);
    }

    /// <summary>Every <c>applicableClauses[].ref</c> must be a <c>POL-n</c> issued for this run.</summary>
    private static IReadOnlyList<string> ReferenceErrors(JsonElement output, ReferenceRegistry references)
    {
        var refs = output.GetProperty("applicableClauses").EnumerateArray().Select(c => c.GetProperty("ref").GetString() ?? string.Empty);
        return references.Validate(refs, ReferenceKind.Policy).Select(e => $"/applicableClauses: {e}").ToList();
    }

    private static int? Confidence(JsonElement value)
        => value.TryGetInt32(out var whole) ? whole : value.TryGetDouble(out var number) ? (int)Math.Round(number, MidpointRounding.AwayFromZero) : null;

    /// <summary>Case facts for the user turn: product, dates and region only — never customer identifiers (FR-006a).</summary>
    private static string CaseFacts(CaseContext @case, Product? product)
    {
        var name = product?.Name ?? @case.Product?.Name;
        var category = product?.Category ?? @case.Product?.Category;
        var facts = new StringBuilder("Case facts:\n");
        facts.Append(name is null
            ? "- Product: not found in the catalog\n"
            : string.Create(CultureInfo.InvariantCulture, $"- Product: {name} (category: {category ?? "unknown"})\n"));
        facts.Append(CultureInfo.InvariantCulture, $"- Product model code: {@case.ProductModelCode}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Region: {(@case.Region ?? @case.Customer.Region)?.ToString() ?? "unknown"}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Purchase date: {Iso(@case.PurchaseDate)}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Claim date: {Iso(@case.ClaimDate)}");
        return facts.ToString();
    }

    /// <summary>
    /// The intake reading: its enumerated fields as facts; symptoms and summary — derived from the claimant's
    /// text — only as untrusted content (FR-019).
    /// </summary>
    private static IReadOnlyList<AiContentPart> IntakeReading(IntakeExtraction? extraction)
    {
        if (extraction is null)
        {
            return [new TextPart("Intake reading: not available. Use the component UNKNOWN for warranty_lookup.")];
        }

        var reading = new StringBuilder("Intake reading:\n");
        reading.Append(CultureInfo.InvariantCulture, $"- Component: {extraction.Component}\n");
        reading.Append(CultureInfo.InvariantCulture, $"- Problem category: {extraction.ProblemCategory}\n");
        reading.Append(CultureInfo.InvariantCulture, $"- Claimed cause: {extraction.ClaimedCause}\n");
        reading.Append(CultureInfo.InvariantCulture, $"- Mentions accident: {Bool(extraction.MentionsAccident)}\n");
        reading.Append(CultureInfo.InvariantCulture, $"- Mentions liquid: {Bool(extraction.MentionsLiquid)}");

        var summary = new StringBuilder();
        if (extraction.Symptoms.Count > 0)
        {
            summary.Append("Symptoms: ").Append(string.Join("; ", extraction.Symptoms)).Append('\n');
        }

        summary.Append("Summary: ").Append(extraction.Summary);
        return [new TextPart(reading.ToString()), new UntrustedTextPart(IntakeSummaryLabel, summary.ToString())];
    }

    /// <summary>A clause as presented to the model (contracts/rag.md rule 7, plus section and type).</summary>
    public static string Render(string refId, RetrievedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var to = chunk.EffectiveTo is { } end ? Iso(end) : "open";
        var from = chunk.EffectiveFrom is { } start ? Iso(start) : "unknown";
        var type = chunk.ClauseType is { } clauseType
            ? chunk.ExclusionCode is { } code ? $"{clauseType} ({WireName.Of(code)})" : clauseType.ToString()
            : "unknown";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{refId}] {chunk.ClauseKey} — {chunk.DocumentTitle} (v{chunk.Version}, effective {from}–{to})\nSection: {chunk.SectionTitle ?? "—"} · Type: {type}\n{chunk.Text}");
    }

    private static string Render(IssuedClause clause) => Render(clause.Ref, clause.Chunk);

    /// <summary><c>POWER_FAILURE</c> → <c>power failure</c>.</summary>
    private static string Humanize(string value) => value.Replace('_', ' ').ToLowerInvariant();

    private static string Bool(bool value) => value ? "yes" : "no";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record Retrieval(RetrievalOutcome Outcome, IReadOnlyList<RetrievedChunk> Chunks, PolicyVersion? Version);

    private sealed record IssuedClause(string Ref, RetrievedChunk Chunk);

    /// <summary>The model step's outcome: the validated assessment, or a failing status with diagnostics.</summary>
    private sealed record Reasoning(
        AgentStatus Status,
        string? Json,
        int? Confidence,
        string Model,
        string? CoverageAssessment,
        bool IsAmbiguous,
        string? AmbiguityExplanation,
        IReadOnlyList<string> Diagnostics)
    {
        public static Reasoning Failed(AgentStatus status, params string[] diagnostics)
            => new(status, null, null, string.Empty, null, false, null, diagnostics);
    }
}

/// <summary>Natural order of clause keys: <c>AUR-WP-2.9</c> before <c>AUR-WP-2.10</c>; null keys last.</summary>
public sealed class ClauseKeyComparer : IComparer<string?>
{
    public static ClauseKeyComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : 1) : -1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byNumber = numberX.Length != numberY.Length
                    ? numberX.Length.CompareTo(numberY.Length)
                    : string.CompareOrdinal(numberX, numberY);
                if (byNumber != 0)
                {
                    return byNumber;
                }

                continue;
            }

            var byChar = x[i].CompareTo(y[j]);
            if (byChar != 0)
            {
                return byChar;
            }

            i++;
            j++;
        }

        var byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }
}
