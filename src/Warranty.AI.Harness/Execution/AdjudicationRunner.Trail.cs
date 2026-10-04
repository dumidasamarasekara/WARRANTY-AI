using System.Globalization;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Common;
using Warranty.Guardrails;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Execution;

/// <summary>
/// Decision-trail entries of the harness steps (FR-038). Every payload names the run and round; payloads
/// carry codes, reference IDs and counts, never customer identifiers or the claimant's own text.
/// </summary>
public sealed partial class AdjudicationRunner
{
    private static List<TrailEntry> IntakeEntries(RunState state, IntakeResult intake)
    {
        var ctx = state.Context;
        var failed = intake.Validation.Where(v => !v.Passed).Select(v => v.Check).ToList();
        var missing = intake.MissingItems.Select(i => i.Item).ToList();
        var summary = failed.Count == 0 && missing.Count == 0
            ? Invariant($"Intake validation passed: {intake.Validation.Count} checks, nothing missing.")
            : Invariant($"Intake validation: {intake.Validation.Count - failed.Count} of {intake.Validation.Count} checks passed")
              + (missing.Count > 0 ? $"; missing: {string.Join(", ", missing)}." : ".");
        var entries = new List<TrailEntry>
        {
            new(
                TrailStep.IntakeValidated,
                AgentNames.Intake,
                summary,
                new
                {
                    runId = ctx.RunId,
                    round = ctx.Round,
                    checks = intake.Validation.Select(v => new { check = v.Check, passed = v.Passed, detail = v.Detail }).ToList(),
                    missingItems = missing,
                    shortCircuit = state.ShortCircuit,
                }),
        };

        if (IntakeExtraction.From(intake) is { } extraction)
        {
            entries.Add(new(
                TrailStep.ClaimExtracted,
                AgentNames.Intake,
                $"Problem structured: {extraction.ProblemCategory} of component {extraction.Component}.",
                new
                {
                    runId = ctx.RunId,
                    round = ctx.Round,
                    problemCategory = extraction.ProblemCategory,
                    component = extraction.Component,
                    claimedCause = extraction.ClaimedCause,
                    symptomCount = extraction.Symptoms.Count,
                    mentionsAccident = extraction.MentionsAccident,
                    mentionsLiquid = extraction.MentionsLiquid,
                    containsInstructionsToSystem = extraction.ContainsInstructionsToSystem,
                }));
        }

        var customer = ctx.Case.Customer;
        entries.Add(new(
            TrailStep.CustomerVerified,
            AgentNames.Intake,
            customer.Region is { } region
                ? $"Customer record found (country {customer.Country}, region {region})."
                : $"Customer record found (country {customer.Country}); region not determined from the record.",
            new { runId = ctx.RunId, round = ctx.Round, verified = true, country = customer.Country, region = customer.Region?.ToString() }));

        var product = ctx.Case.Product;
        entries.Add(new(
            TrailStep.ProductIdentified,
            AgentNames.Intake,
            product is not null
                ? $"Product identified: {product.Name} ({product.ModelCode}), category {product.Category}; the serial number is registered."
                : $"Product {ctx.Case.ProductModelCode} with serial {ctx.Case.SerialNumber} was not found in the catalog.",
            new
            {
                runId = ctx.RunId,
                round = ctx.Round,
                productInCatalog = product is not null,
                productId = product?.ProductId,
                modelCode = ctx.Case.ProductModelCode,
                serialNumber = ctx.Case.SerialNumber,
                productName = product?.Name,
                category = product?.Category,
                claimValue = product?.ClaimValue,
            }));

        return entries;
    }

    private static TrailEntry EvidenceEntry(RunState state, EvidenceResult evidence)
    {
        var ctx = state.Context;
        var refs = ctx.References.Entries.Where(e => e.Kind == ReferenceKind.Evidence).ToDictionary(e => e.TargetId, e => e.Id);
        var files = ctx.Case.Evidence.Count(e => e.Kind is Domain.Claims.EvidenceKind.Invoice or Domain.Claims.EvidenceKind.Photo);
        var conflicts = evidence.ConsistencyChecks.Count(c => !c.Match && c.ClaimValue is not null && c.EvidenceValue is not null);
        var missing = evidence.MissingItems.Select(i => i.Item).ToList();
        var summary = Invariant($"{evidence.Findings.Count} of {files} evidence files analysed; {evidence.ConsistencyChecks.Count} consistency checks, {conflicts} conflicting")
                      + (missing.Count > 0 ? $"; missing: {string.Join(", ", missing)}." : ".");
        return new(
            TrailStep.EvidenceAnalyzed,
            AgentNames.Evidence,
            summary,
            new
            {
                runId = ctx.RunId,
                round = ctx.Round,
                findings = evidence.Findings.Select(f => new
                {
                    evidenceRef = refs.GetValueOrDefault(f.EvidenceId),
                    evidenceId = f.EvidenceId,
                    kind = f.Kind.ToString(),
                    confidence = f.Confidence,
                }).ToList(),
                consistencyChecks = evidence.ConsistencyChecks.Select(c => new { field = c.Field, match = c.Match }).ToList(),
                validation = evidence.Validation.Select(v => new { check = v.Check, passed = v.Passed }).ToList(),
                missingItems = missing,
            });
    }

    private static List<TrailEntry> PolicyEntries(RunState state, PolicyResult policy)
    {
        var ctx = state.Context;
        var outcome = WireName.Of(policy.VersionOutcome);
        var title = policy.Clauses.FirstOrDefault()?.DocumentTitle;
        var retrieved = policy.VersionOutcome switch
        {
            PolicyVersionOutcome.Ok => Invariant($"Policy {title ?? "version"} v{policy.Version!.Version} applies; {policy.Clauses.Count} clauses issued")
                                       + (policy.Clauses.Count > 0 ? $" ({string.Join(", ", policy.Clauses.Select(c => $"{c.RefId} {c.ClauseKey}"))})." : "."),
            PolicyVersionOutcome.AmbiguousPolicyVersion => "More than one policy version could apply to the purchase date.",
            _ => "No applicable policy version was found for the purchase.",
        };

        var window = policy.CoverageWindow;
        var coverage = window.Outcome == CoverageWindowOutcome.Determined
            ? Invariant($"Coverage window ends {window.CoverageEndDate:yyyy-MM-dd}: the claim date is {(window.WithinComponentCoverage == true ? "within" : "outside")} coverage")
            : "Coverage window not determined";
        coverage += policy.CoverageAssessment is { } assessed ? $"; policy reading: {assessed}." : ".";

        return
        [
            new(
                TrailStep.PolicyRetrieved,
                AgentNames.Policy,
                retrieved,
                new
                {
                    runId = ctx.RunId,
                    round = ctx.Round,
                    versionOutcome = outcome,
                    policyVersionId = policy.Version?.Id,
                    version = policy.Version?.Version,
                    clauses = policy.Clauses.Select(c => new
                    {
                        refId = c.RefId,
                        clauseKey = c.ClauseKey,
                        clauseType = c.ClauseType.ToString(),
                        exclusionCode = c.ExclusionCode is { } code ? WireName.Of(code) : null,
                        documentTitle = c.DocumentTitle,
                        version = c.Version,
                        score = c.Score,
                    }).ToList(),
                }),
            new(
                TrailStep.CoverageAssessed,
                AgentNames.Policy,
                coverage,
                new
                {
                    runId = ctx.RunId,
                    round = ctx.Round,
                    component = policy.Component,
                    coverageWindow = new
                    {
                        outcome = window.Outcome.ToString(),
                        coverageEndDate = window.CoverageEndDate,
                        withinStandardCoverage = window.WithinStandardCoverage,
                        withinComponentCoverage = window.WithinComponentCoverage,
                    },
                    coverageAssessment = policy.CoverageAssessment,
                    isAmbiguous = policy.IsAmbiguous,
                }),
        ];
    }

    private static TrailEntry RiskEntry(RunState state, RiskAssessment assessment)
    {
        var ctx = state.Context;
        var codes = assessment.Signals.Select(s => WireName.Of(s.Code)).ToList();
        return new(
            TrailStep.RiskEvaluated,
            AgentNames.Risk,
            Invariant($"Risk {assessment.Level} (score {assessment.Score}); ")
            + (codes.Count == 0 ? "no risk signals." : $"signals: {string.Join(", ", codes)}."),
            new
            {
                runId = ctx.RunId,
                round = ctx.Round,
                stage = assessment.Stage.ToString(),
                score = assessment.Score,
                level = assessment.Level.ToString(),
                signals = assessment.Signals.Select(s => new
                {
                    code = WireName.Of(s.Code),
                    source = s.Source.ToString(),
                    severity = s.Severity.ToString(),
                    evidenceRefs = s.EvidenceRefs,
                }).ToList(),
            });
    }

    private static TrailEntry RecommendationEntry(RunState state, Recommendation recommendation)
    {
        var ctx = state.Context;
        var summary = recommendation.IsValid && recommendation.Decision is { } decision
            ? Invariant($"AI recommends {WireName.Of(decision)}")
              + (recommendation.Coverage is { } coverage ? $" ({WireName.Of(coverage)}" : " (")
              + Invariant($", confidence {recommendation.Confidence}).")
            : Invariant($"The AI recommendation is invalid ({recommendation.ValidationErrors.Count} validation errors).");
        return new(
            TrailStep.AiRecommended,
            AgentNames.Decision,
            summary,
            new
            {
                runId = ctx.RunId,
                round = ctx.Round,
                isValid = recommendation.IsValid,
                decision = recommendation.Decision is { } d ? WireName.Of(d) : null,
                coverage = recommendation.Coverage is { } c ? WireName.Of(c) : null,
                confidence = recommendation.Confidence,
                evidenceRefs = recommendation.EvidenceRefs.Select(r => r.Ref).ToList(),
                policyRefs = recommendation.PolicyRefs.Select(r => new { refId = r.Ref, relevance = WireName.Of(r.Relevance) }).ToList(),
                missingInformation = recommendation.MissingInformation.Select(i => i.Item).ToList(),
                manipulationDetected = recommendation.ManipulationDetected,
                validationErrorCount = recommendation.ValidationErrors.Count,
                model = recommendation.Model,
                promptId = recommendation.PromptId,
                promptVersion = recommendation.PromptVersion,
            });
    }

    private static TrailEntry GuardrailsEntry(RunState state, GuardrailOutcome outcome, ApprovedAction action)
    {
        var ctx = state.Context;
        var failed = outcome.Checks.Where(c => !c.Passed).Select(c => WireName.Of(c.Code)).ToList();
        var reasons = outcome.Reasons.Select(WireName.Of).ToList();
        var summary = Invariant($"Guardrails: {outcome.Checks.Count - failed.Count} of {outcome.Checks.Count} checks passed; disposition {WireName.Of(outcome.Disposition)}")
                      + (reasons.Count > 0 ? $" ({string.Join(", ", reasons)})." : ".");
        return new(
            TrailStep.GuardrailsEvaluated,
            SystemActor,
            summary,
            new
            {
                runId = ctx.RunId,
                round = ctx.Round,
                disposition = WireName.Of(outcome.Disposition),
                reasons,
                checks = outcome.Checks.Select(c => new { code = WireName.Of(c.Code), stage = c.Stage, passed = c.Passed }).ToList(),
                action = action.Kind.ToString(),
                requestedItems = action.RequestedItems.Select(i => i.Item).ToList(),
            });
    }

    private static TrailEntry FailureEntry(RunState state, AiStepFailure failure)
        => new(
            TrailStep.AiStepFailed,
            failure.Agent,
            $"The {failure.Agent} step did not complete ({failure.Status}); the claim cannot be finalized automatically.",
            new { runId = state.Context.RunId, round = state.Context.Round, agent = failure.Agent, status = failure.Status.ToString(), reason = failure.Detail });

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
