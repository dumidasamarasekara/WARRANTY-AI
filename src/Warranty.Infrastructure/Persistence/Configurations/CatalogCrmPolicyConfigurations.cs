using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Catalog;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Policies;
using Warranty.Domain.Tenancy;

namespace Warranty.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", "catalog");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(p => p.ModelCode).HasMaxLength(50).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.ModelCode }).IsUnique();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Category).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Currency).HasColumnType("char(3)").IsRequired();
    }
}

internal sealed class ProductSerialConfiguration : IEntityTypeConfiguration<ProductSerial>
{
    public void Configure(EntityTypeBuilder<ProductSerial> builder)
    {
        builder.ToTable("product_serials", "catalog");
        builder.HasKey(s => new { s.TenantId, s.SerialNumber });
        builder.Property(s => s.SerialNumber).HasMaxLength(50);
        builder.HasOne<Product>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers", "crm");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(c => c.FullName).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(320).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.Email }).IsUnique();
        builder.Property(c => c.Phone).HasMaxLength(30);
        builder.Property(c => c.AddressLine).HasMaxLength(200);
        builder.Property(c => c.City).HasMaxLength(100);
        builder.Property(c => c.PostalCode).HasMaxLength(20);
        builder.Property(c => c.Country).HasColumnType("char(2)").IsRequired();
        builder.Property(c => c.Region).HasWireName();
    }
}

internal sealed class WarrantyPolicyConfiguration : IEntityTypeConfiguration<WarrantyPolicy>
{
    public void Configure(EntityTypeBuilder<WarrantyPolicy> builder)
    {
        builder.ToTable("warranty_policies", "policy");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(p => p.Code).HasMaxLength(30).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.Code }).IsUnique();
        builder.Property(p => p.Title).HasMaxLength(200).IsRequired();
    }
}

internal sealed class PolicyVersionConfiguration : IEntityTypeConfiguration<PolicyVersion>
{
    public void Configure(EntityTypeBuilder<PolicyVersion> builder)
    {
        builder.ToTable("policy_versions", "policy");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.HasAlternateKey(v => new { v.TenantId, v.Id });
        builder.HasOne<WarrantyPolicy>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.PolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(v => new { v.PolicyId, v.Version }).IsUnique();
        builder.PrimitiveCollection(v => v.Regions).ElementType(e => e.HasConversion<string>());
        builder.PrimitiveCollection(v => v.ProductCategories);
        builder.Property(v => v.Terms).HasJsonConversion();
        builder.Property(v => v.SourceBlobPath).HasMaxLength(500).IsRequired();
        builder.Property(v => v.Checksum).HasMaxLength(64).IsRequired();
    }
}

internal sealed class PolicyClauseConfiguration : IEntityTypeConfiguration<PolicyClause>
{
    public void Configure(EntityTypeBuilder<PolicyClause> builder)
    {
        builder.ToTable("policy_clauses", "policy");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasOne<PolicyVersion>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.PolicyVersionId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.PolicyVersionId, c.ClauseKey }).IsUnique();
        builder.Property(c => c.ClauseKey).HasMaxLength(50).IsRequired();
        builder.Property(c => c.ClauseType).HasWireName();
        builder.Property(c => c.ExclusionCode).HasWireName();
        builder.Property(c => c.Title).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Text).IsRequired();
    }
}
