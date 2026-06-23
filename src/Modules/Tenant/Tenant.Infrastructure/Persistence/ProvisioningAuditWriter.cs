using BuildingBlocks.Infrastructure.Persistence;
using Tenant.Application;
using Tenant.Domain;

namespace Tenant.Infrastructure.Persistence;

/// <summary>Stages a <see cref="ProvisioningAudit"/> on the (admin) producer context; committed in the
/// provisioning transaction.</summary>
public sealed class ProvisioningAuditWriter : IProvisioningAuditWriter
{
    private readonly ProducerDbContext _db;

    public ProvisioningAuditWriter(ProducerDbContext db) => _db = db;

    public void Append(ProvisioningAudit entry) => _db.Set<ProvisioningAudit>().Add(entry);
}
