using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Crm;
using Warranty.Domain.Tenancy;

namespace Warranty.Infrastructure.Persistence.Configurations;

internal sealed class ClaimConfiguration : IEntityTypeConfiguration<Claim>
{
    /// <summary>Shadow property holding PostgreSQL's <c>xmin</c>; exposed as the claim ETag for review decisions.</summary>
    public const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<Claim> builder)
    {
        builder.ToTable("claims", "claims");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.CustomerId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(c => c.Reference).HasColumnType("char(10)").IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.Reference }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.SerialNumber });
        builder.HasIndex(c => new { c.TenantId, c.Status });
        builder.Property(c => c.Channel).HasWireName();
        builder.Property(c => c.SubmittedBy).HasMaxLength(200).IsRequired();
        builder.Property(c => c.ContactEmail).HasMaxLength(320);
        builder.Property(c => c.ContactPhone).HasMaxLength(30);
        builder.Property(c => c.ProductModelCode).HasMaxLength(50).IsRequired();
        builder.Property(c => c.SerialNumber).HasMaxLength(50).IsRequired();
        builder.Property(c => c.PurchasePlace).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Region).HasWireName();
        builder.Property(c => c.ProblemDescription).HasMaxLength(Claim.MaxDescriptionLength).IsRequired();
        builder.Property(c => c.Status).HasWireName();
        builder.Property(c => c.FinalOutcome).HasWireName();
        builder.Property(c => c.FinalDecidedBy).HasWireName();
        builder.Property(c => c.RequestedItems)
            .HasField("_requestedItems")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasJsonConversion();

        builder.Property<uint>(RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}

internal sealed class ClaimEvidenceConfiguration : IEntityTypeConfiguration<ClaimEvidence>
{
    public void Configure(EntityTypeBuilder<ClaimEvidence> builder)
    {
        builder.ToTable("claim_evidence", "claims");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(e => e.Kind).HasWireName();
        builder.Property(e => e.FileName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Sha256).HasColumnType("char(64)").IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.Sha256 });
        builder.Property(e => e.BlobPath).HasMaxLength(300).IsRequired();
    }
}

internal sealed class ClaimJobConfiguration : IEntityTypeConfiguration<ClaimJob>
{
    public void Configure(EntityTypeBuilder<ClaimJob> builder)
    {
        builder.ToTable("claim_jobs", "claims");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(j => new { j.TenantId, j.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(j => new { j.ClaimId, j.Round }).IsUnique();
        builder.HasIndex(j => new { j.Status, j.AvailableAt });
        builder.Property(j => j.Status).HasWireName();
        builder.Property(j => j.CorrelationId).HasMaxLength(64).IsRequired();
    }
}
