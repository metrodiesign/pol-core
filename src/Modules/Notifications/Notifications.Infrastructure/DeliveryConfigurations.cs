using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Domain;

namespace Notifications.Infrastructure;

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.ToTable("WebhookEndpoints", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(160).IsRequired(); b.Property(x => x.Url).HasMaxLength(2048).IsRequired();
        b.Property(x => x.EventsCsv).HasMaxLength(2000).IsRequired(); b.Property(x => x.SecretHint).HasMaxLength(32).IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.MerchantId, x.Enabled });
        TenantKeyDescriptor.Require(b.Metadata, nameof(WebhookEndpoint.MerchantId));
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("WebhookDeliveries", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(160).IsRequired(); b.Property(x => x.TransactionId).HasMaxLength(200);
        b.Property(x => x.Payload).IsRequired(); b.Property(x => x.ReplayKey).HasMaxLength(200);
        b.Property(x => x.Status).HasConversion<int>(); b.Property(x => x.FailureCode).HasMaxLength(120);
        b.Property(x => x.LeaseOwner).HasMaxLength(200); b.HasIndex(x => new { x.Status, x.NextAttemptAt, x.LeaseExpiresAt });
        b.HasIndex(x => new { x.EndpointId, x.SourceEventId }).IsUnique().HasFilter("[OriginalDeliveryId] IS NULL");
        b.HasIndex(x => new { x.OriginalDeliveryId, x.ReplayKey }).IsUnique().HasFilter("[OriginalDeliveryId] IS NOT NULL");
        b.HasIndex(x => new { x.MerchantId, x.Status, x.CreatedAt });
        TenantKeyDescriptor.Require(b.Metadata, nameof(WebhookDelivery.MerchantId));
    }
}

internal sealed class NotificationRuleConfiguration : IEntityTypeConfiguration<NotificationRule>
{
    public void Configure(EntityTypeBuilder<NotificationRule> b)
    {
        b.ToTable("NotificationRules", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(160).IsRequired(); b.Property(x => x.Channel).HasMaxLength(32).IsRequired();
        b.Property(x => x.Destination).HasMaxLength(2048).IsRequired(); b.Property(x => x.Threshold).HasMaxLength(200);
        b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.MerchantId, x.Enabled });
        TenantKeyDescriptor.Require(b.Metadata, nameof(NotificationRule.MerchantId));
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> b)
    {
        b.ToTable("NotificationDeliveries", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(160).IsRequired(); b.Property(x => x.Channel).HasMaxLength(32).IsRequired();
        b.Property(x => x.DestinationMasked).HasMaxLength(256).IsRequired(); b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.FailureCode).HasMaxLength(120); b.HasIndex(x => new { x.MerchantId, x.SentAt });
        b.HasIndex(x => new { x.RuleId, x.SourceEventId }).IsUnique();
        TenantKeyDescriptor.Require(b.Metadata, nameof(NotificationDelivery.MerchantId));
        AppendOnlyDescriptor.Mark(b.Metadata);
    }
}

internal sealed class DeliverySecretVersionConfiguration : IEntityTypeConfiguration<DeliverySecretVersion>
{
    public void Configure(EntityTypeBuilder<DeliverySecretVersion> b)
    {
        b.ToTable("DeliverySecretVersions", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.OwnerType).HasMaxLength(64).IsRequired(); b.Property(x => x.ProtectedSecret).HasMaxLength(4096).IsRequired();
        b.Property(x => x.State).HasConversion<int>(); b.HasIndex(x => new { x.OwnerType, x.OwnerId, x.State });
        TenantKeyDescriptor.Require(b.Metadata, nameof(DeliverySecretVersion.MerchantId));
    }
}
