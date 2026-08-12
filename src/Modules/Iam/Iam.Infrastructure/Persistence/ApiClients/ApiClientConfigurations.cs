using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.ApiClients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iam.Infrastructure.Persistence.ApiClients;

internal sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        builder.ToTable("ApiClients", SchemaNames.Iam);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PublicClientId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.ScopesCsv).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.IpPolicy).HasMaxLength(2000);
        builder.Property(x => x.SecretHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SecretHint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.HasIndex(x => x.PublicClientId).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.Status });
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.PendingRotationApprovalId).IsUnique()
            .HasFilter("[PendingRotationApprovalId] IS NOT NULL");
    }
}

internal sealed class OneTimeSecretTicketConfiguration : IEntityTypeConfiguration<OneTimeSecretTicket>
{
    public void Configure(EntityTypeBuilder<OneTimeSecretTicket> builder)
    {
        builder.ToTable("OneTimeSecretTickets", SchemaNames.Iam);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TicketHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ProtectedSecret).HasMaxLength(4096);
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.TicketHash).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasIndex(x => x.ApprovalId).IsUnique().HasFilter("[ApprovalId] IS NOT NULL");
    }
}
