using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Audit;
using Warranty.Domain.Claims;
using Warranty.Domain.Review;
using Warranty.Domain.Tenancy;

namespace Warranty.Infrastructure.Persistence.Configurations;

internal sealed class ReviewDecisionConfiguration : IEntityTypeConfiguration<ReviewDecision>
{
    public void Configure(EntityTypeBuilder<ReviewDecision> builder)
    {
        builder.ToTable("review_decisions", "review");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AdjudicationRun>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.RunId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(d => new { d.TenantId, d.ClaimId });
        builder.Property(d => d.ReviewerSub).HasMaxLength(200).IsRequired();
        builder.Property(d => d.ReviewerName).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Decision).HasWireName();
        builder.Property(d => d.Justification).HasMaxLength(ReviewDecision.MaxJustificationLength);
        builder.Property(d => d.ClaimantExplanation).HasMaxLength(ReviewDecision.MaxClaimantExplanationLength);
        builder.Property(d => d.RequestedItems).HasJsonConversion();
    }
}

internal sealed class DecisionTrailEntryConfiguration : IEntityTypeConfiguration<DecisionTrailEntry>
{
    public void Configure(EntityTypeBuilder<DecisionTrailEntry> builder)
    {
        builder.ToTable("decision_trail_entries", "audit");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.ClaimId, e.Seq }).IsUnique();
        builder.Property(e => e.Step).HasWireName();
        builder.Property(e => e.Actor).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(500).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(64);
        builder.Property(e => e.PrevHash).HasColumnType("char(64)").IsRequired();
        builder.Property(e => e.Hash).HasColumnType("char(64)").IsRequired();
    }
}

internal sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("security_events", "audit");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
        builder.Property(e => e.Kind).HasWireName();
        builder.Property(e => e.Actor).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Target).HasMaxLength(300);
        builder.Property(e => e.SourceIp).HasMaxLength(45);
    }
}
