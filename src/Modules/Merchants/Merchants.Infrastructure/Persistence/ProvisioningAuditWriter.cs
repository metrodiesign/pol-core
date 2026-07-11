using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Application;
using Merchants.Domain;

namespace Merchants.Infrastructure.Persistence;

/// <summary>Stages a <see cref="ProvisioningAudit"/> on the (admin) PolDbContext; committed in the
/// provisioning transaction.</summary>
public sealed class ProvisioningAuditWriter : IProvisioningAuditWriter
{
    private readonly PolDbContext _db;

    public ProvisioningAuditWriter(PolDbContext db) => _db = db;

    public void Append(ProvisioningAudit entry) => _db.Set<ProvisioningAudit>().Add(entry);
}
