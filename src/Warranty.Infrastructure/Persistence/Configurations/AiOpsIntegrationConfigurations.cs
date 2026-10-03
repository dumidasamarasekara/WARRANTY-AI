using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.AiOps;
using Warranty.Domain.Claims;
using Warranty.Domain.Integration;
using Warranty.Domain.Tenancy;

namespace Warranty.Infrastructure.Persistence.Configurations;

internal sealed class ModelCallConfiguration : IEntityTypeConfiguration<ModelCall>
{
    public void Configure(EntityTypeBuilder<ModelCall> builder)
    {
        builder.ToTable("model_calls", "aiops");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.RunId });
        builder.Property(c => c.Agent).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Route).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Provider).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Model).HasMaxLength(100).IsRequired();
        builder.Property(c => c.PromptId).HasMaxLength(50);
        builder.Property(c => c.PromptVersion).HasMaxLength(50);
        builder.Property(c => c.EstimatedCost).HasPrecision(12, 6);
        builder.Property(c => c.StopReason).HasMaxLength(50);
        builder.Property(c => c.Status).HasWireName();
        builder.Property(c => c.CorrelationId).HasMaxLength(64);
    }
}

internal sealed class ToolCallConfiguration : IEntityTypeConfiguration<ToolCall>
{
    public void Configure(EntityTypeBuilder<ToolCall> builder)
    {
        builder.ToTable("tool_calls", "aiops");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.RunId });
        builder.Property(c => c.Agent).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Tool).HasMaxLength(50).IsRequired();
        builder.Property(c => c.DenialReason).HasMaxLength(300);
        builder.Property(c => c.ResultSummary).HasMaxLength(1000);
        builder.Property(c => c.Status).HasMaxLength(30).IsRequired();
    }
}

internal sealed class RagQueryConfiguration : IEntityTypeConfiguration<RagQuery>
{
    public void Configure(EntityTypeBuilder<RagQuery> builder)
    {
        builder.ToTable("rag_queries", "aiops");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(q => q.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(q => new { q.TenantId, q.RunId });
        builder.Property(q => q.Agent).HasMaxLength(50).IsRequired();
        builder.PrimitiveCollection(q => q.Namespaces);
    }
}

internal sealed class ServiceCenterConfiguration : IEntityTypeConfiguration<ServiceCenter>
{
    public void Configure(EntityTypeBuilder<ServiceCenter> builder)
    {
        builder.ToTable("service_centers", "integration");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasAlternateKey(s => new { s.TenantId, s.Id });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.Region).HasWireName();
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.PrimitiveCollection(s => s.Capabilities);
    }
}

internal sealed class RepairRequestConfiguration : IEntityTypeConfiguration<RepairRequest>
{
    public void Configure(EntityTypeBuilder<RepairRequest> builder)
    {
        builder.ToTable("repair_requests", "integration");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ServiceCenter>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ServiceCenterId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(r => r.Status).HasWireName();
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications", "integration");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.HasOne<Claim>().WithMany()
            .HasForeignKey(n => new { n.TenantId, n.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(n => n.Channel).HasWireName();
        builder.Property(n => n.Template).HasMaxLength(100).IsRequired();
    }
}
