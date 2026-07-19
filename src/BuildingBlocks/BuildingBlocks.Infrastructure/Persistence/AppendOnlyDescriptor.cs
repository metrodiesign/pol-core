using Microsoft.EntityFrameworkCore.Metadata;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Marks an entity type append-only at model-build time (rls-to-query-filter REQ-2.4 — VaultRevealAudit/
/// ProvisioningAudit — and the "append-only audits" clause of the MerchantUser/ControlPlane write-floor
/// guards). <see cref="GuardedRuntimeDbContext"/> rejects Modified/Deleted for any entity carrying this
/// annotation regardless of what <c>IWriteAuthorizer</c> would otherwise allow — an audit trail row that
/// can be edited or removed after the fact is not an audit trail.
/// </summary>
public static class AppendOnlyDescriptor
{
    internal const string AnnotationName = "Pol:AppendOnly";

    public static void Mark(IMutableEntityType entityType) => entityType.SetAnnotation(AnnotationName, true);
}
