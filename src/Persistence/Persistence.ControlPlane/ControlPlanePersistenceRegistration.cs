using Admins.Application.Roles;
using Admins.Application.Users;
using BuildingBlocks.Application;
using Iam.Application.Roles;
using MasterData.Application;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.DataProtection;
using Persistence.ControlPlane.Iam;
using IamRoleStore = Persistence.ControlPlane.Iam.RoleStore;
using ControlPlaneMasterDataStore = Persistence.ControlPlane.MasterData.MasterDataStore;

namespace Persistence.ControlPlane;

/// <summary>
/// The ONE public seam onto <see cref="ControlPlaneDbContext"/> (task 8.5.1, design.md "Assembly split + Api
/// host boundary" — no <c>InternalsVisibleTo(Api)</c>, so every adapter that names the internal context lives
/// in this assembly and is exposed here only through public application-layer ports). Registers the context
/// itself (unkeyed — ControlPlane carries no RLS session-context interceptor, unlike the pre-cutover
/// <c>PolDbContext</c>) plus every adapter over it.
/// </summary>
public static class ControlPlanePersistenceRegistration
{
    public static IServiceCollection AddControlPlanePersistence(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IWriteAuthorizer> authorizerFactory)
    {
        services.AddScoped(sp =>
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(connectionString)
                .UseApplicationServiceProvider(sp)
                .Options;
            return new ControlPlaneDbContext(options, authorizerFactory(sp), sp.GetRequiredService<ISecurityTelemetry>());
        });

        services.AddScoped<IUserRepository>(sp => new UserRepository(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ILogger<UserRepository>>()));
        services.AddScoped<IAuditWriter>(sp => new AuditWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<ISessionStore>(sp => new SessionStore(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<IAuthAuditWriter>(sp => new AuthAuditWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IMasterDataLookup>(sp => new MasterDataLookup(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IRoleRepository>(sp => new RoleRepository(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddScoped<IRoleStore>(sp => new IamRoleStore(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ILogger<IamRoleStore>>()));

        services.AddKeyedScoped<IUnitOfWork>("admin", (sp, _) =>
            new ControlPlaneUnitOfWork(
                sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));

        // MasterDataStore commits through the keyed "admin" IUnitOfWork above, same discipline as every other
        // ControlPlane write seam.
        services.AddScoped<IMasterDataStore>(sp => new ControlPlaneMasterDataStore(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredKeyedService<IUnitOfWork>("admin")));

        services.AddScoped<IAdminRoleAssignmentCountReader>(sp =>
            new AdminRoleAssignmentCountReader(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddScoped<IMerchantRoleReader>(sp =>
            new MerchantRoleReader(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddSingleton<EfCoreXmlRepository>();
        services.AddSingleton<IXmlRepository>(sp => sp.GetRequiredService<EfCoreXmlRepository>());
        services.AddSingleton<IPersistedXmlRepository>(sp => sp.GetRequiredService<EfCoreXmlRepository>());

        return services;
    }
}
