using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.Infrastructure.Persistence.Configurations;

internal sealed class AdjudicationRunConfiguration : IEntityTypeConfiguration<AdjudicationRun>
{
    public void Configure(EntityTypeBuilder<AdjudicationRun> builder)
    {
        builder.ToTable("adjudication_runs", "adjudication");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.ClaimId, r.Round }).IsUnique();
        builder.Property(r => r.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Status).HasWireName();
        builder.Property(r => r.CurrentStep).HasWireName();
        builder.Property(r => r.Disposition).HasWireName();
        builder.Property(r => r.ReferenceMap)
            .HasField("_referenceMap")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasJsonConversion();
    }
}

internal static class RunChildMapping
{
    /// <summary>Maps a per-run result keyed by run ID with a tenant-inclusive FK to its run.</summary>
    public static void MapOnePerRun<T>(EntityTypeBuilder<T> builder, string table, System.Linq.Expressions.Expression<Func<T, object?>> key)
        where T : class
    {
        builder.ToTable(table, "adjudication");
        builder.HasKey(key);
        builder.HasOne<AdjudicationRun>().WithOne()
            .HasForeignKey<T>("TenantId", "RunId")
            .HasPrincipalKey<AdjudicationRun>(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IntakeResultConfiguration : IEntityTypeConfiguration<IntakeResult>
{
    public void Configure(EntityTypeBuilder<IntakeResult> builder)
    {
        RunChildMapping.MapOnePerRun(builder, "intake_results", i => i.RunId);
        builder.Property(i => i.Validation).HasJsonConversion();
        builder.Property(i => i.MissingItems).HasJsonConversion();
        builder.Ignore(i => i.IsComplete);
    }
}

internal sealed class EvidenceFindingConfiguration : IEntityTypeConfiguration<EvidenceFinding>
{
    public void Configure(EntityTypeBuilder<EvidenceFinding> builder)
    {
        builder.ToTable("evidence_findings", "adjudication");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.HasOne<AdjudicationRun>().WithMany()
            .HasForeignKey(f => new { f.TenantId, f.RunId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ClaimEvidence>().WithMany()
            .HasForeignKey(f => new { f.TenantId, f.EvidenceId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(f => f.Kind).HasWireName();
        builder.Property(f => f.Consistency).HasJsonConversion();
    }
}

internal sealed class RetrievedPolicyRefConfiguration : IEntityTypeConfiguration<RetrievedPolicyRef>
{
    public void Configure(EntityTypeBuilder<RetrievedPolicyRef> builder)
    {
        builder.ToTable("retrieved_policy_refs", "adjudication");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasOne<AdjudicationRun>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.RunId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.RunId, r.RefId }).IsUnique();
        builder.Property(r => r.RefId).HasMaxLength(20).IsRequired();
        builder.Property(r => r.ClauseKey).HasMaxLength(50).IsRequired();
        builder.Property(r => r.ClauseType).HasWireName();
        builder.Property(r => r.ExclusionCode).HasWireName();
        builder.Property(r => r.DocumentTitle).HasMaxLength(200).IsRequired();
    }
}

internal sealed class PolicyAssessmentConfiguration : IEntityTypeConfiguration<PolicyAssessment>
{
    public void Configure(EntityTypeBuilder<PolicyAssessment> builder)
    {
        RunChildMapping.MapOnePerRun(builder, "policy_assessments", a => a.RunId);
        builder.Property(a => a.VersionOutcome).HasWireName();
        builder.Property(a => a.Model).HasMaxLength(100);
        builder.Property(a => a.PromptVersion).HasMaxLength(50);
    }
}

internal sealed class RiskAssessmentConfiguration : IEntityTypeConfiguration<RiskAssessment>
{
    public void Configure(EntityTypeBuilder<RiskAssessment> builder)
    {
        RunChildMapping.MapOnePerRun(builder, "risk_assessments", r => r.RunId);
        builder.Property(r => r.Stage).HasWireName();
        builder.Property(r => r.Level).HasWireName();
        builder.Property(r => r.Signals).HasJsonConversion();
    }
}

internal sealed class RecommendationConfiguration : IEntityTypeConfiguration<Recommendation>
{
    public void Configure(EntityTypeBuilder<Recommendation> builder)
    {
        RunChildMapping.MapOnePerRun(builder, "recommendations", r => r.RunId);
        builder.Property(r => r.ValidationErrors).HasJsonConversion();
        builder.Property(r => r.Decision).HasWireName();
        builder.Property(r => r.Coverage).HasWireName();
        builder.Property(r => r.EvidenceRefs).HasJsonConversion();
        builder.Property(r => r.PolicyRefs).HasJsonConversion();
        builder.Property(r => r.MissingInformation).HasJsonConversion();
        builder.Property(r => r.Model).HasMaxLength(100);
        builder.Property(r => r.PromptId).HasMaxLength(50);
        builder.Property(r => r.PromptVersion).HasMaxLength(50);
    }
}

internal sealed class GuardrailEvaluationConfiguration : IEntityTypeConfiguration<GuardrailEvaluation>
{
    public void Configure(EntityTypeBuilder<GuardrailEvaluation> builder)
    {
        RunChildMapping.MapOnePerRun(builder, "guardrail_evaluations", g => g.RunId);
        builder.Property(g => g.Checks).HasJsonConversion();
        builder.Property(g => g.Disposition).HasWireName();
        builder.Property(g => g.Reasons).HasJsonConversion();
    }
}
