using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain.Routing;

namespace Merchants.Infrastructure.Persistence;

/// <summary>Cross-owner database constraint; routing behavior remains Payments-owned.</summary>
public sealed class RoutingOriginatorConfiguration : IEntityTypeConfiguration<RoutingRule>
{
    public void Configure(EntityTypeBuilder<RoutingRule> builder) =>
        builder.HasOne<Originator>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.OriginatorId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
}
