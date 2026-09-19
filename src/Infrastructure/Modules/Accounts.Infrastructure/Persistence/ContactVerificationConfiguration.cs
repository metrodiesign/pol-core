using Accounts.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Accounts.Infrastructure.Persistence;

public sealed class ContactVerificationConfiguration : IEntityTypeConfiguration<ContactVerification>
{
    public void Configure(EntityTypeBuilder<ContactVerification> builder)
    {
        builder.ToTable("ContactVerifications", SchemaNames.Acct);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Recipient).HasMaxLength(10).IsRequired();
        builder.Property(x => x.CodeHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => new { x.RegistrationId, x.CreatedAt });
        builder.HasIndex(x => new { x.Recipient, x.CreatedAt });
        builder.HasOne<AgentRegistration>().WithMany().HasForeignKey(x => x.RegistrationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
