using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;

namespace Persistence.ControlPlane.Payments;

internal sealed class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
    public void Configure(EntityTypeBuilder<PaymentMethod> builder)
    {
        builder.ToTable("PaymentMethods", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasData(
            Method(PaymentCapabilityIds.Card, PaymentMethods.Card, "Card"),
            Method(PaymentCapabilityIds.PromptPay, PaymentMethods.PromptPay, "PromptPay"),
            Method(PaymentCapabilityIds.Installment, PaymentMethods.Installment, "Installment"));
    }

    private static object Method(Guid id, string code, string name) => new
    {
        Id = id, Code = code, Name = name, IsActive = true,
        UpdatedBy = (Guid?)null, UpdatedAt = (DateTime?)null, Version = 1L,
    };
}

internal sealed class PaymentMethodOptionGroupConfiguration : IEntityTypeConfiguration<PaymentMethodOptionGroup>
{
    public void Configure(EntityTypeBuilder<PaymentMethodOptionGroup> builder)
    {
        builder.ToTable("PaymentMethodOptionGroups", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.HasIndex(x => new { x.PaymentMethodId, x.Code }).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.PaymentMethodId });
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasData(new
        {
            Id = PaymentCapabilityIds.InstallmentBankGroup,
            PaymentMethodId = PaymentCapabilityIds.Installment,
            Code = "BANK",
            Name = "Bank",
        });
    }
}

internal sealed class PaymentMethodOptionConfiguration : IEntityTypeConfiguration<PaymentMethodOption>
{
    public void Configure(EntityTypeBuilder<PaymentMethodOption> builder)
    {
        builder.ToTable("PaymentMethodOptions", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.HasIndex(x => new { x.OptionGroupId, x.Code }).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.PaymentMethodId });
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethodOptionGroup>().WithMany()
            .HasForeignKey(x => new { x.OptionGroupId, x.PaymentMethodId })
            .HasPrincipalKey(x => new { x.Id, x.PaymentMethodId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasData(
            Option(PaymentCapabilityIds.Kbank, "KBANK"),
            Option(PaymentCapabilityIds.Scb, "SCB"),
            Option(PaymentCapabilityIds.Ktc, "KTC"),
            Option(PaymentCapabilityIds.Bay, "BAY"));
    }

    private static object Option(Guid id, string code) => new
    {
        Id = id,
        PaymentMethodId = PaymentCapabilityIds.Installment,
        OptionGroupId = PaymentCapabilityIds.InstallmentBankGroup,
        Code = code,
        Name = code,
    };
}

internal sealed class PaymentProviderConfiguration : IEntityTypeConfiguration<PaymentProvider>
{
    public void Configure(EntityTypeBuilder<PaymentProvider> builder)
    {
        builder.ToTable("PaymentProviders", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.AdapterCode).HasConversion<int>().IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => x.AdapterCode).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.AdapterCode });
        builder.HasData(
            Provider(PaymentCapabilityIds.TwoCTwoP, "2c2p", Code.TwoCTwoP, "2C2P"),
            Provider(PaymentCapabilityIds.Omise, "omise", Code.Omise, "Omise"));
    }

    private static object Provider(Guid id, string code, Code adapterCode, string name) => new
    {
        Id = id,
        Code = code,
        AdapterCode = adapterCode,
        Name = name,
        IsEnabled = true,
        UpdatedBy = (Guid?)null,
        UpdatedAt = (DateTime?)null,
        Version = 1L,
    };
}

internal sealed class PaymentProviderMethodConfiguration : IEntityTypeConfiguration<PaymentProviderMethod>
{
    public void Configure(EntityTypeBuilder<PaymentProviderMethod> builder)
    {
        builder.ToTable("PaymentProviderMethods", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.CreatedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.PaymentProviderId, x.PaymentMethodId }).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.PaymentMethodId });
        builder.HasAlternateKey(x => new { x.Id, x.PaymentProviderId, x.PaymentMethodId });
        builder.HasOne<PaymentProvider>().WithMany().HasForeignKey(x => x.PaymentProviderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasData(
            ProviderMethod(PaymentCapabilityIds.TwoCTwoPCard, PaymentCapabilityIds.TwoCTwoP, PaymentCapabilityIds.Card),
            ProviderMethod(PaymentCapabilityIds.TwoCTwoPPromptPay, PaymentCapabilityIds.TwoCTwoP, PaymentCapabilityIds.PromptPay),
            ProviderMethod(PaymentCapabilityIds.TwoCTwoPInstallment, PaymentCapabilityIds.TwoCTwoP, PaymentCapabilityIds.Installment),
            ProviderMethod(PaymentCapabilityIds.OmiseCard, PaymentCapabilityIds.Omise, PaymentCapabilityIds.Card));
    }

    private static object ProviderMethod(Guid id, Guid providerId, Guid methodId) => new
    {
        Id = id,
        PaymentProviderId = providerId,
        PaymentMethodId = methodId,
        IsActive = true,
        CreatedBy = PaymentCapabilityIds.SeedActor,
        CreatedAt = PaymentCapabilityIds.SeededAt,
        UpdatedBy = (Guid?)null,
        UpdatedAt = (DateTime?)null,
        Version = 1L,
    };
}

internal sealed class PaymentProviderMethodOptionConfiguration
    : IEntityTypeConfiguration<PaymentProviderMethodOption>
{
    public void Configure(EntityTypeBuilder<PaymentProviderMethodOption> builder)
    {
        builder.ToTable("PaymentProviderMethodOptions", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.CreatedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.PaymentProviderMethodId, x.PaymentMethodOptionId }).IsUnique();
        builder.HasAlternateKey(x => new
        {
            x.Id,
            x.PaymentProviderMethodId,
            x.PaymentMethodId,
            x.PaymentMethodOptionId,
        });
        builder.HasOne<PaymentProviderMethod>().WithMany()
            .HasForeignKey(x => new { x.PaymentProviderMethodId, x.PaymentMethodId })
            .HasPrincipalKey(x => new { x.Id, x.PaymentMethodId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethodOption>().WithMany()
            .HasForeignKey(x => new { x.PaymentMethodOptionId, x.PaymentMethodId })
            .HasPrincipalKey(x => new { x.Id, x.PaymentMethodId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentAuthorizationStateConfiguration(ControlPlaneDbContext context)
    : IEntityTypeConfiguration<PaymentAuthorizationState>
{
    public void Configure(EntityTypeBuilder<PaymentAuthorizationState> builder)
    {
        var singletonId = context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? PaymentCapabilityIds.AuthorizationState.ToString("D").ToUpperInvariant()
            : PaymentCapabilityIds.AuthorizationState.ToString("D");
        builder.ToTable("PaymentAuthorizationStates", SchemaNames.Cfg, table =>
            table.HasCheckConstraint("CK_PaymentAuthorizationStates_Singleton",
                $"[Id] = '{singletonId}'"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Mode).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasData(new
        {
            Id = PaymentCapabilityIds.AuthorizationState,
            Mode = PaymentAuthorizationMode.LegacyRead,
            CutoffAt = (DateTime?)null,
            Version = 1L,
        });
    }
}

internal sealed class PaymentCapabilityMigrationConflictConfiguration
    : IEntityTypeConfiguration<PaymentCapabilityMigrationConflict>
{
    public void Configure(EntityTypeBuilder<PaymentCapabilityMigrationConflict> builder)
    {
        builder.ToTable("PaymentCapabilityMigrationConflicts", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.Detail).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.DetectedAt).IsRequired();
        builder.HasIndex(x => new { x.ResolvedAt, x.Kind });
    }
}
