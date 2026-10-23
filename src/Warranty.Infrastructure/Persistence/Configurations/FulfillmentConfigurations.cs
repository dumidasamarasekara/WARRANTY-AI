using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;
using Warranty.Domain.Policies;

namespace Warranty.Infrastructure.Persistence.Configurations;

// Remedies and repairs (feature 002 data-model.md). Every table is tenant-owned: composite foreign
// keys include tenant_id, the context adds the tenant query filter and 001_roles_and_rls.sql adds RLS
// (and, for audit.repair_events, the append-only trigger) because the tables live in existing schemas.

internal sealed class ClaimRemedyConfiguration : IEntityTypeConfiguration<ClaimRemedy>
{
    /// <summary>Shadow property holding PostgreSQL's <c>xmin</c>; exposed as the remedy ETag.</summary>
    public const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<ClaimRemedy> builder)
    {
        builder.ToTable("claim_remedies", "claims");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PolicyVersion>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.PolicyVersionId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.ClaimId }).IsUnique();
        builder.Property(r => r.Scenario).HasWireName();
        builder.Property(r => r.State).HasWireName();
        builder.Property(r => r.Remedy).HasWireName();
        builder.PrimitiveCollection(r => r.AllowedRemedies)
            .HasField("_allowedRemedies")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .ElementType(e => e.HasConversion<string>());
        builder.Property(r => r.PendingReason).HasWireName();
        builder.Property<uint>(RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}

internal sealed class RemedyDecisionConfiguration : IEntityTypeConfiguration<RemedyDecision>
{
    public void Configure(EntityTypeBuilder<RemedyDecision> builder)
    {
        builder.ToTable("remedy_decisions", "claims");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PolicyVersion>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.PolicyVersionId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(d => new { d.TenantId, d.ClaimId, d.Seq }).IsUnique();
        builder.Property(d => d.Kind).HasWireName();
        builder.Property(d => d.Scenario).HasWireName();
        builder.Property(d => d.TableRow).HasMaxLength(30).IsRequired();
        builder.PrimitiveCollection(d => d.AllowedRemedies)
            .HasField("_allowedRemedies")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .ElementType(e => e.HasConversion<string>());
        builder.Property(d => d.Remedy).HasWireName();
        builder.Property(d => d.DecidedBy).HasWireName();
        builder.Property(d => d.Actor).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Reason).HasMaxLength(RemedyDecision.MaxReasonLength);
    }
}

internal sealed class RemedyCompletionConfiguration : IEntityTypeConfiguration<RemedyCompletion>
{
    public void Configure(EntityTypeBuilder<RemedyCompletion> builder)
    {
        builder.ToTable("remedy_completions", "claims");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.ClaimId }).IsUnique();
        builder.Property(c => c.Remedy).HasWireName();
        builder.Property(c => c.Reference).HasMaxLength(100);
        builder.Property(c => c.RecordedBy).HasMaxLength(200).IsRequired();
    }
}

internal sealed class RepairConfiguration : IEntityTypeConfiguration<Repair>
{
    /// <summary>Shadow property holding PostgreSQL's <c>xmin</c>; exposed as the repair ETag.</summary>
    public const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<Repair> builder)
    {
        builder.ToTable("repairs", "claims");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.ClaimId }).IsUnique();
        builder.Property(r => r.Status).HasWireName();
        builder.Property(r => r.NotPossibleReason).HasWireName();

        // active_handover_id and current_attempt_id point at rows of this aggregate's own child tables.
        // They are plain columns without a database foreign key: the child rows reference the repair, so
        // a constraint in the other direction would form a cycle EF Core cannot order when the repair and
        // its first hand-over/attempt are inserted together. The aggregate keeps them consistent.
        builder.Ignore(r => r.ActiveHandover);
        builder.Ignore(r => r.CurrentAttempt);

        builder.HasMany(r => r.Handovers).WithOne()
            .HasForeignKey(h => new { h.TenantId, h.RepairId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Handovers).HasField("_handovers").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(r => r.Attempts).WithOne()
            .HasForeignKey(a => new { a.TenantId, a.RepairId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Attempts).HasField("_attempts").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(r => r.Events).WithOne()
            .HasForeignKey(e => new { e.TenantId, e.RepairId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Events).HasField("_events").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property<uint>(RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}

internal sealed class RepairHandoverConfiguration : IEntityTypeConfiguration<RepairHandover>
{
    /// <summary>Partial unique index: at most one active hand-over per repair.</summary>
    public const string OneActivePerRepairIndex = "ux_repair_handovers_one_active";

    public void Configure(EntityTypeBuilder<RepairHandover> builder)
    {
        builder.ToTable("repair_handovers", "claims");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.HasOne<ServiceCenter>().WithMany()
            .HasForeignKey(h => new { h.TenantId, h.ServiceCenterId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(h => new { h.TenantId, h.RepairId })
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName(OneActivePerRepairIndex);
        builder.Property(h => h.Assignment).HasWireName();
        builder.Property(h => h.CentreReference).HasMaxLength(RepairHandover.MaxCentreReferenceLength);
        builder.Property(h => h.Note).HasMaxLength(RepairHandover.MaxNoteLength);
        builder.Property(h => h.Reason).HasMaxLength(1000);
        builder.Property(h => h.RecordedBy).HasMaxLength(200).IsRequired();
    }
}

internal sealed class RepairAttemptConfiguration : IEntityTypeConfiguration<RepairAttempt>
{
    public void Configure(EntityTypeBuilder<RepairAttempt> builder)
    {
        builder.ToTable("repair_attempts", "claims");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.HasOne<ServiceCenter>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ServiceCenterId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.RepairId, a.Seq }).IsUnique();
        builder.Property(a => a.Result).HasWireName();
        builder.Property(a => a.FailureReason).HasMaxLength(1000);
        builder.Ignore(a => a.IsOpen);
    }
}

internal sealed class RepairEventConfiguration : IEntityTypeConfiguration<RepairEvent>
{
    public void Configure(EntityTypeBuilder<RepairEvent> builder)
    {
        builder.ToTable("repair_events", "audit");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
        builder.HasOne<RepairEvent>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.CorrectsEventId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.TenantId, e.RepairId, e.Seq }).IsUnique();
        builder.Property(e => e.Type).HasWireName();
        builder.Property(e => e.FromStatus).HasWireName();
        builder.Property(e => e.ToStatus).HasWireName();
        builder.Property(e => e.Source).HasWireName();
        builder.Property(e => e.Actor).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(1000);
        builder.Property(e => e.PayloadJson).IsRequired();
    }
}
