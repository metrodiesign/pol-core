using System.Xml.Linq;
using BuildingBlocks.Infrastructure.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Persistence.ControlPlane.DataProtection;

/// <summary>
/// Persists the ASP.NET Core Data Protection key ring in <c>dbo.DataProtectionKeys</c> via
/// <see cref="ControlPlaneDbContext"/> — moved from <c>Api.Admins.EfCoreXmlRepository</c> (task 8.5.1,
/// design.md "Assembly split + Api host boundary": the host must not resolve <c>ControlPlaneDbContext</c>
/// directly, so this type moves into this assembly and is exposed only via
/// <c>ControlPlanePersistenceRegistration</c> over the opaque <see cref="IXmlRepository"/> interface).
/// <see cref="IXmlRepository"/> is synchronous and the repository is a singleton, so it opens a fresh DI
/// scope per call to reach the Scoped context. The framework only ever appends keys + reads them (never
/// updates/deletes).
/// </summary>
/// <summary>Public marker so a host outside this assembly (which cannot name the internal
/// <see cref="EfCoreXmlRepository"/> directly, task 8.5.7) can assert the control-plane XML repository is
/// actually the one bound to <c>KeyManagementOptions.XmlRepository</c> — without gaining any other access to
/// the internal type.</summary>
public interface IPersistedXmlRepository;

internal sealed class EfCoreXmlRepository : IXmlRepository, IPersistedXmlRepository
{
    private readonly IServiceScopeFactory _scopeFactory;

    public EfCoreXmlRepository(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        return db.DataProtectionKeys.AsNoTracking()
            .Select(k => k.Xml)
            .ToList()
            .Select(XElement.Parse)
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.DataProtectionKeys.Add(new DataProtectionKey
        {
            SecretKey = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        db.SaveChanges();
    }
}
