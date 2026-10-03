using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.Knowledge.Ingestion;

/// <summary>
/// Checks a parsed source before anything is written (contracts/rag.md, research R26): the namespace
/// is <c>global</c> (platform ingestion, no tenant context) or exactly the namespace of the tenant
/// being ingested; a policy version does not overlap another version of the same policy; the
/// <c>clauses</c> map types every <c>##</c> heading and nothing else; and every <c>Exclusion</c>
/// clause names an exclusion code listed in <c>terms.exclusions</c>. All problems are reported at once.
/// </summary>
public sealed class KnowledgeSourceValidator(ITenantContext tenantContext)
{
    public const string GlobalNamespace = "global";

    /// <param name="otherVersions">The already ingested versions of the same policy (empty for other documents).</param>
    public void Validate(KnowledgeSource source, IReadOnlyList<KnowledgeSection> sections, IReadOnlyList<PolicyVersion> otherVersions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(otherVersions);
        var errors = new List<string>();

        ValidateNamespace(source, errors);
        if (source.Version < 1)
        {
            errors.Add("version must be 1 or higher.");
        }

        if (source.Classification is null)
        {
            errors.Add("classification is required.");
        }

        if (source.AllowedRoles.Count == 0)
        {
            errors.Add("allowedRoles must name at least one role, or no one could retrieve the document.");
        }

        if (source.EffectiveFrom is { } from && source.EffectiveTo is { } to && to < from)
        {
            errors.Add($"effectiveTo {to:yyyy-MM-dd} is before effectiveFrom {from:yyyy-MM-dd}.");
        }

        if (source.ProductCategories.Count > 1)
        {
            errors.Add("productCategories may name at most one category per document (an empty list means all).");
        }

        if (source.DocumentType == DocumentType.WarrantyPolicy)
        {
            ValidatePolicy(source, sections, otherVersions, errors);
        }
        else
        {
            if (source.PolicyCode is not null || source.Terms is not null || source.Clauses.Count > 0)
            {
                errors.Add("policyCode, terms and clauses are only allowed on WarrantyPolicy documents.");
            }

            if (sections.Count == 0)
            {
                errors.Add("the document has no text.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidKnowledgeSourceException(source.SourcePath, errors);
        }
    }

    private void ValidateNamespace(KnowledgeSource source, List<string> errors)
    {
        if (source.Namespace == GlobalNamespace)
        {
            if (tenantContext.IsResolved)
            {
                errors.Add($"global knowledge is ingested by the platform, not for tenant '{tenantContext.TenantSlug}'.");
            }

            if (source.DocumentType == DocumentType.WarrantyPolicy)
            {
                errors.Add("warranty policies belong to a tenant namespace, not 'global'.");
            }

            return;
        }

        if (!tenantContext.IsResolved)
        {
            errors.Add($"namespace '{source.Namespace}' needs the tenant being ingested; only 'global' is ingested without one.");
        }
        else if (source.Namespace != tenantContext.KnowledgeNamespace)
        {
            errors.Add($"namespace '{source.Namespace}' is neither 'global' nor '{tenantContext.KnowledgeNamespace}', the tenant being ingested.");
        }
    }

    private static void ValidatePolicy(
        KnowledgeSource source, IReadOnlyList<KnowledgeSection> sections, IReadOnlyList<PolicyVersion> otherVersions, List<string> errors)
    {
        if (source.PolicyCode is null)
        {
            errors.Add("policyCode is required for a WarrantyPolicy.");
        }

        if (source.EffectiveFrom is not { } from)
        {
            errors.Add("effectiveFrom is required for a WarrantyPolicy.");
        }
        else
        {
            var to = source.EffectiveTo ?? DateOnly.MaxValue;
            foreach (var other in otherVersions.Where(v => v.Version != source.Version && from <= (v.EffectiveTo ?? DateOnly.MaxValue) && v.EffectiveFrom <= to))
            {
                errors.Add($"version {source.Version} overlaps version {other.Version} ({other.EffectiveFrom:yyyy-MM-dd}–{other.EffectiveTo?.ToString("yyyy-MM-dd") ?? "open"}).");
            }
        }

        if (source.Regions.Count == 0)
        {
            errors.Add("regions must name at least one region for a WarrantyPolicy.");
        }

        if (source.Terms is not { } terms)
        {
            errors.Add("terms are required for a WarrantyPolicy.");
        }
        else
        {
            try
            {
                terms.Validate(source.Regions);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"terms: {ex.Message}");
            }
        }

        if (sections.Count == 0)
        {
            errors.Add("the policy has no clauses.");
        }

        var headings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (section.ClauseKey is null)
            {
                errors.Add("text before the first '##' clause heading belongs to no clause.");
                continue;
            }

            if (!headings.Add(section.ClauseKey))
            {
                errors.Add($"clause {section.ClauseKey} has more than one heading.");
            }

            if (section.Text.Length == 0)
            {
                errors.Add($"clause {section.ClauseKey} has no text.");
            }

            if (!source.Clauses.ContainsKey(section.ClauseKey))
            {
                errors.Add($"clause {section.ClauseKey} has no type in the clauses map.");
            }
        }

        foreach (var (key, clause) in source.Clauses)
        {
            if (!headings.Contains(key))
            {
                errors.Add($"clauses.{key} has no '## {key} …' heading.");
            }

            if (clause.Type == ClauseType.Exclusion)
            {
                if (clause.ExclusionCode is not { } code)
                {
                    errors.Add($"exclusion clause {key} must name an exclusionCode.");
                }
                else if (source.Terms is { } listed && !listed.Excludes(code))
                {
                    errors.Add($"exclusion clause {key} names {WireName.Of(code)}, which terms.exclusions does not list.");
                }
            }
            else if (clause.ExclusionCode is not null)
            {
                errors.Add($"clause {key} is {clause.Type}; only Exclusion clauses name an exclusionCode.");
            }
        }
    }
}
