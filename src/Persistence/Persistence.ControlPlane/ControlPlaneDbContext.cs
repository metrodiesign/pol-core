using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Provisioning;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using MasterData.Domain;
using MasterData.Domain.Divisions;
using MasterData.Domain.Levels;
using MasterData.Domain.Offices;
using MasterData.Domain.Positions;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Iam;
using Persistence.ControlPlane.MasterData;

namespace Persistence.ControlPlane;

/// <summary>
/// Runtime context for the ControlPlane co-commit cluster — admin.* + iam.* + cfg.* + dbo.DataProtectionKeys
/// (rls-to-query-filter design.md "Context topology"; handlers 1-15 of the transaction inventory are
/// single-context here). <c>internal sealed</c>: only this assembly's host-registration extension may
/// construct it, so merchant-side code cannot even name this type (REQ-11.8). No migrations declared
/// here — <c>BuildingBlocks.Infrastructure.Persistence.PolDbContext</c> stays the single migration owner
/// and keeps the full relational model (real cross-context FKs); this context's entity configurations are
/// deliberately narrower (see each Configure() for what was dropped and why).
/// </summary>
internal sealed class ControlPlaneDbContext : GuardedRuntimeDbContext
{
    public ControlPlaneDbContext(
        DbContextOptions<ControlPlaneDbContext> options, IWriteAuthorizer authorizer, ISecurityTelemetry telemetry)
        : base(options, authorizer, telemetry) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<MerchantAccess> MerchantAccess => Set<MerchantAccess>();
    public DbSet<Audit> UserAudits => Set<Audit>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AuthAudit> AuthAudits => Set<AuthAudit>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<PermissionGroup> PermissionGroups => Set<PermissionGroup>();
    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Office> Offices => Set<Office>();
    public DbSet<Level> Levels => Set<Level>();
    public DbSet<Division> Divisions => Set<Division>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<ProvisioningOperation> ProvisioningOperations => Set<ProvisioningOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new MerchantAccessConfiguration());
        modelBuilder.ApplyConfiguration(new AuditConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new AuthAuditConfiguration());
        modelBuilder.ApplyConfiguration(new RoleAssignmentConfiguration());

        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new PermissionGroupConfiguration());
        modelBuilder.ApplyConfiguration(new PermissionConfiguration());

        modelBuilder.ApplyConfiguration(new MasterDataConfiguration());
        modelBuilder.ApplyConfiguration(new PositionConfiguration());
        modelBuilder.ApplyConfiguration(new OfficeConfiguration());
        modelBuilder.ApplyConfiguration(new LevelConfiguration());
        modelBuilder.ApplyConfiguration(new DivisionConfiguration());

        modelBuilder.ApplyConfiguration(new DataProtectionKeyConfiguration());
        modelBuilder.ApplyConfiguration(new ProvisioningOperationConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
