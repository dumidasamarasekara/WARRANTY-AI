using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;
using Warranty.Domain.Policies;

namespace Warranty.Infrastructure.Persistence.Configurations;

// Replacement fulfilment and faulty-product return (feature 003 data-model.md). Every table is
// tenant-owned: composite foreign keys include tenant_id, the context adds the tenant query filter and
// 001_roles_and_rls.sql adds RLS (and, for audit.replacement_events, the append-only trigger) because the
// tables live in existing schemas.

internal sealed class ReplacementConfiguration : IEntityTypeConfiguration<Replacement>
{
    /// <summary>Shadow property holding PostgreSQL's <c>xmin</c>; exposed as the replacement ETag.</summary>
    public const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<Replacement> builder)
    {
        builder.ToTable("replacements", "claims");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.ClaimId }).IsUnique();
        builder.HasOne<PolicyVersion>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.PolicyVersionId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ApprovedProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(r => r.ProductRule).HasWireName();
        builder.Property(r => r.ReturnRule).HasWireName();
        builder.Property(r => r.Status).HasWireName();
        builder.Property(r => r.ReturnState).HasWireName();

        // active_location_id, current_unit_id and pending_proposal_id point at rows of this aggregate's own
        // child tables. They are plain columns without a database foreign key: the child rows reference the
        // replacement, so a constraint in the other direction would form a cycle EF Core cannot order when
        // the replacement and its first location/unit/proposal are inserted together. The aggregate keeps
        // them consistent (as 002's repairs.active_handover_id).
        builder.Ignore(r => r.ActiveLocation);
        builder.Ignore(r => r.CurrentUnit);
        builder.Ignore(r => r.PendingProposal);

        builder.HasMany(r => r.Locations).WithOne()
            .HasForeignKey(l => new { l.TenantId, l.ReplacementId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Locations).HasField("_locations").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne(r => r.Return).WithOne()
            .HasForeignKey<FaultyProductReturn>(f => new { f.TenantId, f.ReplacementId })
            .HasPrincipalKey<Replacement>(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Proposals).WithOne()
            .HasForeignKey(p => new { p.TenantId, p.ReplacementId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Proposals).HasField("_proposals").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(r => r.Units).WithOne()
            .HasForeignKey(u => new { u.TenantId, u.ReplacementId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Units).HasField("_units").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(r => r.Events).WithOne()
            .HasForeignKey(e => new { e.TenantId, e.ReplacementId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Events).HasField("_events").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property<uint>(RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}

internal sealed class ReplacementLocationConfiguration : IEntityTypeConfiguration<ReplacementLocation>
{
    /// <summary>Partial unique index: at most one active location per replacement.</summary>
    public const string OneActivePerReplacementIndex = "ux_replacement_locations_one_active";

    public void Configure(EntityTypeBuilder<ReplacementLocation> builder)
    {
        builder.ToTable("replacement_locations", "claims");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.HasOne<ServiceCenter>().WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ServiceCenterId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => new { l.TenantId, l.ReplacementId })
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName(OneActivePerReplacementIndex);
        builder.Property(l => l.Assignment).HasWireName();
        builder.Property(l => l.Reason).HasMaxLength(ReplacementLocation.MaxReasonLength);
        builder.Property(l => l.RecordedBy).HasMaxLength(200).IsRequired();
    }
}

internal sealed class FaultyProductReturnConfiguration : IEntityTypeConfiguration<FaultyProductReturn>
{
    public void Configure(EntityTypeBuilder<FaultyProductReturn> builder)
    {
        builder.ToTable("faulty_product_returns", "claims");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.HasIndex(f => new { f.TenantId, f.ReplacementId }).IsUnique();
        builder.HasOne<ServiceCenter>().WithMany()
            .HasForeignKey(f => new { f.TenantId, f.ServiceCenterId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RepairHandover>().WithMany()
            .HasForeignKey(f => new { f.TenantId, f.RepairHandoverId })
            .HasPrincipalKey(h => new { h.TenantId, h.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(f => f.SerialSeen).HasMaxLength(FaultyProductReturn.MaxSerialLength);
        builder.Property(f => f.Note).HasMaxLength(FaultyProductReturn.MaxNoteLength);
        builder.Property(f => f.RecordedBy).HasMaxLength(200).IsRequired();
        builder.Ignore(f => f.IsHeldByRepairCentre);
    }
}

internal sealed class ReplacementModelProposalConfiguration : IEntityTypeConfiguration<ReplacementModelProposal>
{
    /// <summary>Partial unique index: at most one pending proposal per replacement.</summary>
    public const string OnePendingPerReplacementIndex = "ux_replacement_model_proposals_one_pending";

    public void Configure(EntityTypeBuilder<ReplacementModelProposal> builder)
    {
        builder.ToTable("replacement_model_proposals", "claims");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasOne<Product>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TenantId, p.ReplacementId })
            .IsUnique()
            .HasFilter("state = 'Pending'")
            .HasDatabaseName(OnePendingPerReplacementIndex);
        builder.Property(p => p.Reason).HasMaxLength(ReplacementModelProposal.MaxReasonLength).IsRequired();
        builder.Property(p => p.State).HasWireName();
        builder.Property(p => p.ProposedBy).HasMaxLength(200).IsRequired();
        builder.Property(p => p.DecidedBy).HasMaxLength(200);
        builder.Property(p => p.DecisionReason).HasMaxLength(ReplacementModelProposal.MaxReasonLength);
        builder.Ignore(p => p.IsPending);
    }
}

internal sealed class ReplacementUnitConfiguration : IEntityTypeConfiguration<ReplacementUnit>
{
    public void Configure(EntityTypeBuilder<ReplacementUnit> builder)
    {
        builder.ToTable("replacement_units", "claims");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.HasOne<ServiceCenter>().WithMany()
            .HasForeignKey(u => new { u.TenantId, u.ServiceCenterId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany()
            .HasForeignKey(u => new { u.TenantId, u.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(u => new { u.TenantId, u.ReplacementId, u.Seq }).IsUnique();
        builder.Property(u => u.SerialNumber).HasMaxLength(50).IsRequired();
        builder.Property(u => u.Result).HasWireName();
        builder.Property(u => u.ResultReason).HasMaxLength(ReplacementUnit.MaxResultReasonLength);
        builder.Ignore(u => u.IsReserved);
    }
}

internal sealed class RegisteredReplacementSerialConfiguration : IEntityTypeConfiguration<RegisteredReplacementSerial>
{
    public void Configure(EntityTypeBuilder<RegisteredReplacementSerial> builder)
    {
        builder.ToTable("replacement_serials", "catalog");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.SerialNumber).HasMaxLength(50).IsRequired();
        builder.HasIndex(s => new { s.TenantId, s.SerialNumber }).IsUnique();
        builder.Property(s => s.OriginalSerial).HasMaxLength(50).IsRequired();
        builder.HasIndex(s => new { s.TenantId, s.OriginalSerial });
        builder.HasOne<Product>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReplacementEventConfiguration : IEntityTypeConfiguration<ReplacementEvent>
{
    public void Configure(EntityTypeBuilder<ReplacementEvent> builder)
    {
        builder.ToTable("replacement_events", "audit");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
        builder.HasOne<ReplacementEvent>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.CorrectsEventId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.TenantId, e.ReplacementId, e.Seq }).IsUnique();
        builder.Property(e => e.Type).HasWireName();
        builder.Property(e => e.FromStatus).HasWireName();
        builder.Property(e => e.ToStatus).HasWireName();
        builder.Property(e => e.Source).HasWireName();
        builder.Property(e => e.Actor).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(1000);
        builder.Property(e => e.PayloadJson).IsRequired();
    }
}
