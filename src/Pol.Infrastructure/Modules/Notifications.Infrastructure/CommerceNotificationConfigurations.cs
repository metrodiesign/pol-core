using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Domain;

namespace Notifications.Infrastructure;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SourceEventId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(160).IsUnicode(false).IsRequired();
        builder.Property(x => x.PayloadSnapshot).IsUnicode().IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128);
        builder.Property(x => x.OrderNo).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.TransactionNo).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });
        builder.HasIndex(x => x.SourceEventId).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.OrderNo });
        builder.HasIndex(x => new { x.MerchantId, x.TransactionNo });
        builder.HasIndex(x => new { x.MerchantId, x.CorrelationId });
        builder.HasIndex(x => new { x.MerchantId, x.CreatedAt });
        TenantKeyDescriptor.Require(builder.Metadata, nameof(Notification.MerchantId));
    }
}

internal sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    public void Configure(EntityTypeBuilder<TemplateVersion> builder)
    {
        builder.ToTable("TemplateVersions", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EventType).HasMaxLength(160).IsUnicode(false).IsRequired();
        builder.Property(x => x.Channel).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.Locale).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Content).IsUnicode().IsRequired();
        builder.Property(x => x.ReleasedAt).IsRequired();
        builder.HasIndex(x => new { x.EventType, x.Channel, x.Version, x.Locale }).IsUnique();
    }
}

internal sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        builder.ToTable("Deliveries", SchemaNames.Txn, table =>
        {
            table.HasCheckConstraint("CK_Deliveries_Status", "[Status] IN (1, 2, 3, 4, 5, 6, 7, 8)");
            table.HasCheckConstraint("CK_Deliveries_Channel", "[Channel] IN ('email', 'sms', 'business_webhook')");
            table.HasCheckConstraint("CK_Deliveries_AttemptCount", "[AttemptCount] >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.NotificationId).IsRequired();
        builder.Property(x => x.SourceEventId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Channel).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.RecipientSnapshot).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.RecipientFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.TemplateVersionId).IsRequired();
        builder.Property(x => x.TemplateVersion).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.TemplateLocale).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.TemplateSubjectSnapshot).HasMaxLength(256).IsRequired();
        builder.Property(x => x.TemplateContentSnapshot).IsUnicode().IsRequired();
        builder.Property(x => x.EndpointUrlSnapshot).HasMaxLength(2048);
        builder.Property(x => x.ProtectedEndpointSecretSnapshot).IsUnicode();
        builder.Property(x => x.PayloadSnapshot).IsUnicode().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.NextAttemptAt).IsRequired();
        builder.Property(x => x.LastAttemptAt);
        builder.Property(x => x.CompletedAt);
        builder.Property(x => x.LeaseExpiresAt);
        builder.Property(x => x.LeaseOwner).HasMaxLength(256);
        builder.Property(x => x.ProviderMessageId).HasMaxLength(256).IsUnicode(false);
        builder.Property(x => x.FailureCode).HasMaxLength(128).IsUnicode(false);
        builder.HasIndex(x => new { x.NotificationId, x.Channel, x.RecipientFingerprint }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.Status, x.NextAttemptAt, x.LeaseExpiresAt });
        builder.HasIndex(x => new { x.MerchantId, x.SourceEventId });
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });
        builder.HasOne<Notification>().WithMany()
            .HasForeignKey(x => new { x.NotificationId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TemplateVersion>().WithMany()
            .HasForeignKey(x => x.TemplateVersionId)
            .OnDelete(DeleteBehavior.Restrict);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(Delivery.MerchantId));
    }
}

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        builder.ToTable("DeliveryAttempts", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DeliveryId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.AttemptNo).IsRequired();
        builder.Property(x => x.Outcome).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.ProviderMessageId).HasMaxLength(256).IsUnicode(false);
        builder.Property(x => x.FailureCode).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.LatencyMs);
        builder.Property(x => x.StartedAt).IsRequired();
        builder.Property(x => x.CompletedAt).IsRequired();
        builder.HasIndex(x => new { x.DeliveryId, x.AttemptNo }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.CompletedAt });
        builder.HasOne<Delivery>().WithMany()
            .HasForeignKey(x => new { x.DeliveryId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(DeliveryAttempt.MerchantId));
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}

internal sealed class NotificationInboxMessageConfiguration : IEntityTypeConfiguration<NotificationInboxMessage>
{
    public void Configure(EntityTypeBuilder<NotificationInboxMessage> builder)
    {
        builder.ToTable("NotificationInboxMessages", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SourceEventId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(160).IsUnicode(false).IsRequired();
        builder.Property(x => x.PayloadSnapshot).IsUnicode().IsRequired();
        builder.Property(x => x.ReceivedAt).IsRequired();
        builder.Property(x => x.ProcessedAt);
        builder.HasIndex(x => x.SourceEventId).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.ReceivedAt });
        TenantKeyDescriptor.Require(builder.Metadata, nameof(NotificationInboxMessage.MerchantId));
    }
}

internal sealed class NotificationReviewNoteConfiguration : IEntityTypeConfiguration<NotificationReviewNote>
{
    public void Configure(EntityTypeBuilder<NotificationReviewNote> builder)
    {
        builder.ToTable("NotificationReviewNotes", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.ActorId).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.CreatedAt });
        builder.HasIndex(x => new { x.DeliveryId, x.CreatedAt });
        builder.HasIndex(x => new { x.NotificationId, x.CreatedAt });
        TenantKeyDescriptor.Require(builder.Metadata, nameof(NotificationReviewNote.MerchantId));
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}
