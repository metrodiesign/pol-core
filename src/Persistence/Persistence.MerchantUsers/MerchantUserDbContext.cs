using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantUsers.Users;
// Fully-qualified (not `using`-imported) to avoid colliding with BuildingBlocks.Infrastructure.Outbox's
// same-named migration-owner config (task 8.1) — this context uses its OWN filtered config.
using MerchantUserOutboxConfiguration = Persistence.MerchantUsers.Outbox.MerchantUserOutboxConfiguration;

namespace Persistence.MerchantUsers;

/// <summary>
/// Runtime context for the MerchantUser co-commit cluster — merch.Users/Sessions/ExternalLogins/AuthAudits/
/// RegistrationAudits/RegistrationNotices/RoleAssignments (rls-to-query-filter design.md "Context topology";
/// handlers 17-20 of the transaction inventory are single-context here). internal sealed: only this
/// assembly's host-registration extension may construct it, so control-plane/other module code cannot even
/// name this type. No migrations declared here — PolDbContext stays the single migration owner.
/// </summary>
internal sealed class MerchantUserDbContext : GuardedRuntimeDbContext
{
    private readonly IActorContext _actor;

    public MerchantUserDbContext(
        DbContextOptions<MerchantUserDbContext> options, IActorContext actor, IWriteAuthorizer authorizer,
        ISecurityTelemetry telemetry)
        : base(options, authorizer, telemetry)
        => _actor = actor;

    /// <summary>The read floor's instance member (REQ-1.5), same pattern as
    /// <c>MerchantRuntimeDbContext.CurrentMerchant</c> — only <c>merch.Users</c> and
    /// <c>merch.RoleAssignments</c> reference it (REQ-1.1); the other five entities in this context are not
    /// merchant-filtered this task.</summary>
    internal Guid CurrentMerchant => _actor.HasActor ? _actor.MerchantId : Guid.Empty;

    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<AuthAudit> AuthAudits => Set<AuthAudit>();
    public DbSet<RegistrationAudit> RegistrationAudits => Set<RegistrationAudit>();
    public DbSet<RegistrationNotice> RegistrationNotices => Set<RegistrationNotice>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<MerchantUserOutbox> UserOutbox => Set<MerchantUserOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserConfiguration(this));
        modelBuilder.ApplyConfiguration(new ExternalLoginConfiguration());
        modelBuilder.ApplyConfiguration(new RegistrationAuditConfiguration());
        modelBuilder.ApplyConfiguration(new RegistrationNoticeConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new AuthAuditConfiguration());
        modelBuilder.ApplyConfiguration(new RoleAssignmentConfiguration(this));
        modelBuilder.ApplyConfiguration(new MerchantUserOutboxConfiguration(this));

        base.OnModelCreating(modelBuilder);
    }
}
