using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>Migration-owner mapping for Transaction and its append-only evidence.</summary>
public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("Transactions", SchemaNames.Txn, table =>
        {
            table.HasCheckConstraint("CK_Transactions_Status", "[Status] IN (1, 2, 3, 4, 5, 6)");
            table.HasCheckConstraint("CK_Transactions_AttemptNo", "[AttemptNo] >= 1");
            table.HasCheckConstraint("CK_Transactions_PaymentMethod",
                "[PaymentMethod] IN ('card', 'promptpay', 'installment')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.OrderId).IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(Transaction.MerchantId));

        builder.Property(x => x.TransactionNo).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.AttemptNo).IsRequired();
        builder.ComplexProperty(x => x.Amount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("AmountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("AmountCurrency").HasMaxLength(3)
                .IsFixedLength().IsUnicode(false);
        });
        builder.Property(x => x.PaymentMethod).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.Provider).HasConversion<int>().IsRequired();
        builder.Property(x => x.ProviderAccountId).IsRequired();
        builder.Property(x => x.Environment).HasConversion<int>().IsRequired();
        builder.Property(x => x.CredentialVersionId).IsRequired();
        builder.Property(x => x.ConfigurationVersion).IsRequired();
        builder.Property(x => x.ProviderRequestReference).HasMaxLength(256).IsUnicode(false).IsRequired();
        builder.Property(x => x.ProviderReference).HasMaxLength(256).IsUnicode(false);
        builder.Property(x => x.RedirectUrl).HasMaxLength(2048);
        builder.Property(x => x.ReturnBinding).IsUnicode();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.ProviderStatus).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.OrderSnapshot).IsUnicode().IsRequired();
        builder.Property(x => x.SafeProviderMetadata).HasMaxLength(2000);
        builder.Property(x => x.NeedsReview).IsRequired();
        builder.Property(x => x.ReviewCode).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.Property(x => x.SucceededAt);
        builder.Property(x => x.LastInquiryAt);
        builder.Property(x => x.NextInquiryAt);
        builder.Property(x => x.InquiryAttempts).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => new { x.OrderId, x.AttemptNo }).IsUnique();
        builder.HasIndex(x => x.OrderId, "IX_Transactions_OrderId_Potential")
            .IsUnique().HasFilter("[Status] IN (1, 2)");
        builder.HasIndex(x => new { x.ProviderAccountId, x.Environment, x.ProviderRequestReference })
            .IsUnique();
        builder.HasIndex(x => new { x.ProviderAccountId, x.Environment, x.ProviderReference })
            .IsUnique().HasFilter("[ProviderReference] IS NOT NULL");
        builder.HasIndex(x => x.NextInquiryAt).HasFilter("[NextInquiryAt] IS NOT NULL");
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });
        // The Order aggregate lives in the Commerce cluster. Keep this canonical scalar/table mapping
        // independent of that cross-module relationship so focused migration composition cannot discover a
        // partially configured Order/OrderItem graph. The migration owner and Commerce context add the same
        // relational overlay after both sides are composed.
    }
}

public sealed class TransactionEventConfiguration : IEntityTypeConfiguration<TransactionEvent>
{
    public void Configure(EntityTypeBuilder<TransactionEvent> builder)
    {
        builder.ToTable("TransactionEvents", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.TransactionId).IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(TransactionEvent.MerchantId));
        AppendOnlyDescriptor.Mark(builder.Metadata);
        builder.Property(x => x.Source).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.EventReference).HasMaxLength(256).IsUnicode(false).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.ProviderStatus).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.EvidenceCode).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.SafeDetails).HasMaxLength(2000);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.ReceivedAt).IsRequired();
        builder.HasIndex(x => new { x.TransactionId, x.ReceivedAt });
        builder.HasIndex(x => new { x.TransactionId, x.EventReference, x.Source }).IsUnique();
        builder.HasOne<Transaction>().WithMany()
            .HasForeignKey(x => new { x.TransactionId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
