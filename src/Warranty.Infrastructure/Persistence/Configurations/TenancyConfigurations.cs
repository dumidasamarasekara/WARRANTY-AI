using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Tenancy;

namespace Warranty.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", "tenancy");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Slug).HasMaxLength(31).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();
        builder.Property(t => t.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Status).HasWireName();
        builder.Ignore(t => t.KnowledgeNamespace);
        builder.Ignore(t => t.BlobContainer);
    }
}

internal sealed class TenantChannelConfiguration : IEntityTypeConfiguration<TenantChannel>
{
    public void Configure(EntityTypeBuilder<TenantChannel> builder)
    {
        builder.ToTable("tenant_channels", "tenancy");
        builder.HasKey(c => c.Hostname);
        builder.Property(c => c.Hostname).HasMaxLength(253);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenantSettingsConfiguration : IEntityTypeConfiguration<TenantSettings>
{
    public void Configure(EntityTypeBuilder<TenantSettings> builder)
    {
        builder.ToTable("tenant_settings", "tenancy");
        builder.HasKey(s => s.TenantId);
        builder.HasOne<Tenant>().WithOne().HasForeignKey<TenantSettings>(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.Currency).HasColumnType("char(3)").IsRequired();
        builder.PrimitiveCollection(s => s.AlwaysReviewCategories);
        builder.Property(s => s.Version).IsConcurrencyToken();
    }
}
