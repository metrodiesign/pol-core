using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Persistence.ControlPlane.DataProtection;

namespace Api.Admins;

internal static class AdminDataProtection
{
    /// <summary>Wires Data Protection onto the control-plane key store (task 8.5.7: the concrete
    /// <c>EfCoreXmlRepository</c> is internal to <c>Persistence.ControlPlane</c> — resolve it through the
    /// framework's own <see cref="IXmlRepository"/> instead, registered by AddControlPlanePersistence). Keys
    /// are read lazily on first protect/unprotect, so this does not touch SQL at boot.</summary>
    public static IServiceCollection AddAdminDataProtection(this IServiceCollection services)
    {
        // A fixed application name keeps the key-ring discriminator stable across restarts/instances.
        services.AddDataProtection().SetApplicationName("pol-admin-bff");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IXmlRepository>((options, repository) => options.XmlRepository = repository);
        return services;
    }

    /// <summary>Fail-fast (mirrors the audience/connection guards, REQ-8.2): outside Development the key ring MUST
    /// be the persisted control-plane store, never the framework's default ephemeral/in-memory ring (which would
    /// silently drop every in-flight login on restart and not share across instances). Checks the public
    /// <see cref="IPersistedXmlRepository"/> marker rather than the internal <c>EfCoreXmlRepository</c> type,
    /// which this host may not name (design.md "Assembly split + Api host boundary").</summary>
    public static void RequirePersistentDataProtection(IServiceProvider services)
    {
        var keyManagement = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        if (keyManagement.XmlRepository is not IPersistedXmlRepository)
            throw new InvalidOperationException(
                "Data Protection key ring is not persisted to the control-plane store. The admin OIDC login " +
                "requires AddAdminDataProtection() so correlation cookies survive restarts and span instances.");
    }
}
