using Merchants.Domain;

namespace Merchants.Application;

/// <summary>
/// Stages a <see cref="ProvisioningAudit"/> for insertion in the provisioning transaction. Bound to
/// the pol_admin connection by the host.
/// </summary>
public interface IProvisioningAuditWriter
{
    void Append(ProvisioningAudit entry);
}
