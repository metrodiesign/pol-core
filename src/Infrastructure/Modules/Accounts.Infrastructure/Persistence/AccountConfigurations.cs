using Accounts.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Accounts.Infrastructure.Persistence;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts", SchemaNames.Acct, table => table.HasCheckConstraint(
            "CK_Accounts_AccountType", "[AccountType] IN (1, 2, 3)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.AccountType).HasConversion<int>().IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.AuthorizationVersion).IsConcurrencyToken().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.HasIndex(x => x.Status);
    }
}

public sealed class LoginAccountConfiguration : IEntityTypeConfiguration<LoginAccount>
{
    public void Configure(EntityTypeBuilder<LoginAccount> builder)
    {
        builder.ToTable("LoginAccounts", SchemaNames.Acct);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.AccountId).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ExternalUserId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.DisplayName).HasMaxLength(200);
        builder.Property(x => x.LastLoginAt);
        builder.HasIndex(x => new { x.Provider, x.TenantId, x.ExternalUserId }).IsUnique();
        builder.HasIndex(x => x.AccountId).IsUnique();
    }
}

public sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("Employees", SchemaNames.Acct);
        builder.HasKey(x => x.AccountId);
        builder.Property(x => x.AccountId).ValueGeneratedNever();
        builder.Property(x => x.EmployeeCode).HasMaxLength(128);
        builder.Property(x => x.DepartmentCode).HasMaxLength(128);
        builder.Property(x => x.Metadata).HasColumnType("json").IsRequired();
        builder.HasIndex(x => x.EmployeeCode).IsUnique().HasFilter("[EmployeeCode] IS NOT NULL");
    }
}

public sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("Agents", SchemaNames.Acct);
        builder.HasKey(x => x.AccountId);
        builder.Property(x => x.AccountId).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.SaleId).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("json").IsRequired();
        builder.HasIndex(x => x.SaleId).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.SaleId }).IsUnique();
    }
}

public sealed class SystemClientConfiguration : IEntityTypeConfiguration<SystemClient>
{
    public void Configure(EntityTypeBuilder<SystemClient> builder)
    {
        builder.ToTable("SystemClients", SchemaNames.Acct, table => table.HasCheckConstraint(
            "CK_SystemClients_GrantTypes", "[AllowedGrantTypes] = 'client_credentials'"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.AccountId).IsRequired();
        builder.Property(x => x.ClientId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Environment).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.AllowedGrantTypes).HasMaxLength(128).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.HasIndex(x => x.AccountId).IsUnique();
        builder.HasIndex(x => x.ClientId).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.Environment });
    }
}

public sealed class ClientKeyPolicyConfiguration : IEntityTypeConfiguration<ClientKeyPolicy>
{
    public void Configure(EntityTypeBuilder<ClientKeyPolicy> builder)
    {
        builder.ToTable("ClientKeyPolicies", SchemaNames.Acct, table => table.HasCheckConstraint(
            "CK_ClientKeyPolicies_Validity", "[ValidUntil] IS NULL OR [ValidUntil] > [ValidFrom]"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SystemClientId).IsRequired();
        builder.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.KeyId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Algorithm).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ValidFrom).IsRequired();
        builder.Property(x => x.ValidUntil);
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.AuditReference).HasMaxLength(256);
        builder.HasIndex(x => new { x.ApplicationId, x.KeyId }).IsUnique();
        builder.HasIndex(x => new { x.SystemClientId, x.Status });
    }
}

public sealed class AssertionReplayConfiguration : IEntityTypeConfiguration<AssertionReplay>
{
    public void Configure(EntityTypeBuilder<AssertionReplay> builder)
    {
        builder.ToTable("AssertionReplays", SchemaNames.OAuth);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Jti).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.ConsumedAt).IsRequired();
        builder.HasIndex(x => new { x.ApplicationId, x.Jti }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
    }
}

public sealed class BffSessionTicketConfiguration : IEntityTypeConfiguration<BffSessionTicket>
{
    public void Configure(EntityTypeBuilder<BffSessionTicket> builder)
    {
        builder.ToTable("BffSessionTickets", SchemaNames.Acct);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TicketKeyHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AccountId).IsRequired();
        builder.Property(x => x.ClientId).HasMaxLength(128);
        builder.Property(x => x.ProtectedAuthenticationTicket).IsUnicode().IsRequired();
        builder.Property(x => x.AuthorizationVersion).IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.RevokedAt);
        builder.HasIndex(x => x.TicketKeyHash).IsUnique();
        builder.HasIndex(x => new { x.AccountId, x.RevokedAt });
    }
}

public sealed class RegistrationSessionConfiguration : IEntityTypeConfiguration<RegistrationSession>
{
    public void Configure(EntityTypeBuilder<RegistrationSession> builder)
    {
        builder.ToTable("RegistrationSessions", SchemaNames.Acct);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SessionReferenceHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ExternalUserId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.HasIndex(x => x.SessionReferenceHash).IsUnique();
        builder.HasIndex(x => new { x.Provider, x.TenantId, x.ExternalUserId, x.MerchantId });
    }
}

public sealed class AgentRegistrationConfiguration : IEntityTypeConfiguration<AgentRegistration>
{
    public void Configure(EntityTypeBuilder<AgentRegistration> builder)
    {
        builder.ToTable("AgentRegistrations", SchemaNames.Acct);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ExternalUserId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.CurrentAttemptId);
        builder.Property(x => x.CurrentAttemptNo).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.SaleCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.PhoneNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProfileJson).HasColumnType("json").IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.Provider, x.TenantId, x.ExternalUserId }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.Status, x.UpdatedAt });
    }
}

public sealed class AgentRegistrationAttemptConfiguration : IEntityTypeConfiguration<AgentRegistrationAttempt>
{
    public void Configure(EntityTypeBuilder<AgentRegistrationAttempt> builder)
    {
        builder.ToTable("AgentRegistrationAttempts", SchemaNames.Acct);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.RegistrationId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.AttemptNo).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ExternalUserId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.SaleCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SaleId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.SaleVersion).IsRequired();
        builder.Property(x => x.BranchVersion).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.PhoneNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProfileJson).HasColumnType("json").IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IntentHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.SubmittedAt).IsRequired();
        builder.Property(x => x.DecidedByAccountId);
        builder.Property(x => x.RejectionReason).HasMaxLength(1000);
        builder.Property(x => x.InternalReviewNote).HasMaxLength(4000);
        builder.Property(x => x.ContactEvidenceReference).HasMaxLength(256);
        builder.Property(x => x.DecisionIdempotencyKey).HasMaxLength(200);
        builder.Property(x => x.DecisionIntentHash).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.RegistrationId, x.AttemptNo }).IsUnique();
        builder.HasIndex(x => new { x.RegistrationId, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.Status, x.SubmittedAt });
        builder.HasOne<AgentRegistration>().WithMany().HasForeignKey(x => x.RegistrationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
