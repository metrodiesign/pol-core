using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Infrastructure.Persistence;

public sealed class InboundWebhookEventConfiguration : IEntityTypeConfiguration<InboundWebhookEvent>
{
    public void Configure(EntityTypeBuilder<InboundWebhookEvent> builder)
    {
        builder.ToTable("InboundWebhookEvents", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.PspConnectionId).IsRequired();
        builder.Property(x => x.PaymentSessionId);
        builder.Property(x => x.OrderId);
        builder.Property(x => x.PspCode).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.ExternalEventId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ExternalChargeId).HasMaxLength(256).IsUnicode(false);
        builder.Property(x => x.PayloadFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        // Nullable: null means the provider is fetch-confirm-only (Omise) so the signature is not the
        // authority; true/false is a signed-deterministic (2C2P) verify result (merchant-psp-settings task 8).
        builder.Property(x => x.SignatureValid);
        builder.Property(x => x.VerificationMode).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.FailureCode).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.ReceivedAt).IsRequired();
        builder.Property(x => x.ProcessedAt);
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => new { x.PspConnectionId, x.ExternalEventId }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.ReceivedAt });
        builder.HasIndex(x => new { x.Status, x.ReceivedAt });
        // Rematch lookup for a fetch-confirm-only pending match by the charge it points at (AC-8.4).
        builder.HasIndex(x => new { x.PspConnectionId, x.ExternalChargeId, x.Status });
    }
}
