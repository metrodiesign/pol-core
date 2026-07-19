namespace BuildingBlocks.Application;

/// <summary>
/// The write floor's authorization seam (rls-to-query-filter REQ-2.11, REQ-4.9): every runtime context's
/// sealed <c>SaveChanges</c> guard asks this, per tracked entry, before persisting it. Default-deny — a
/// context only accepts a write when its injected authorizer explicitly grants the (entityType, operation,
/// targetMerchant) tuple under an explicit capability (merchant / worker / provisioning-Super / admin).
/// <para>
/// Host-owned: production registrations differ per flow — a merchant request's capability only covers its
/// own merchant; the provisioning UoW's covers exactly the provisioning entity set under a Super lock;
/// ControlPlaneDbContext's admin capability is implemented via <c>IAdminScope</c> (task 4). targetMerchant
/// is <see cref="Guid.Empty"/> for a context/entity with no tenant key of its own (e.g. every
/// ControlPlaneDbContext entity) — the decision there rests on entityType + operation alone.
/// </para>
/// </summary>
public interface IWriteAuthorizer
{
    bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant);
}
