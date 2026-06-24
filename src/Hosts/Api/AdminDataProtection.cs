using System.Xml.Linq;
using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api;

/// <summary>
/// Persists the ASP.NET Core Data Protection key ring in the control-plane <c>DataProtectionKeys</c> table via
/// the keyed pol_admin <see cref="ProducerDbContext"/> (REQ-8, Tech #5). The OIDC handler's correlation/state/
/// nonce cookies are DP-protected; without a shared, persisted key ring those cookies break across a restart or
/// a second instance. <see cref="IXmlRepository"/> is synchronous and the repository is a singleton, so it opens
/// a fresh DI scope per call to reach the Scoped admin context. The framework only ever appends keys + reads them
/// (never updates/deletes), so the table is granted SELECT, INSERT to pol_admin.
/// </summary>
internal sealed class EfCoreXmlRepository : IXmlRepository
{
    private readonly IServiceScopeFactory _scopeFactory;

    public EfCoreXmlRepository(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredKeyedService<ProducerDbContext>("admin");
        return db.Set<DataProtectionKey>().AsNoTracking()
            .Select(k => k.Xml)
            .ToList()
            .Select(XElement.Parse)
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredKeyedService<ProducerDbContext>("admin");
        db.Set<DataProtectionKey>().Add(new DataProtectionKey
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        db.SaveChanges();
    }
}

internal static class AdminDataProtection
{
    /// <summary>Wires Data Protection onto the control-plane key store. Call AFTER AddTenantAdminScope (the
    /// repository resolves the keyed "admin" context). Keys are read lazily on first protect/unprotect, so this
    /// does not touch SQL at boot.</summary>
    public static IServiceCollection AddAdminDataProtection(this IServiceCollection services)
    {
        services.AddSingleton<EfCoreXmlRepository>();
        // A fixed application name keeps the key-ring discriminator stable across restarts/instances.
        services.AddDataProtection().SetApplicationName("pol-admin-bff");
        services.AddOptions<KeyManagementOptions>()
            .Configure<EfCoreXmlRepository>((options, repository) => options.XmlRepository = repository);
        return services;
    }

    /// <summary>Fail-fast (mirrors the audience/connection guards, REQ-8.2): outside Development the key ring MUST
    /// be the persisted control-plane store, never the framework's default ephemeral/in-memory ring (which would
    /// silently drop every in-flight login on restart and not share across instances).</summary>
    public static void RequirePersistentDataProtection(IServiceProvider services)
    {
        var keyManagement = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        if (keyManagement.XmlRepository is not EfCoreXmlRepository)
            throw new InvalidOperationException(
                "Data Protection key ring is not persisted to the control-plane store. The admin OIDC login " +
                "requires AddAdminDataProtection() so correlation cookies survive restarts and span instances.");
    }
}
