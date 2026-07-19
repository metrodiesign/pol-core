using BuildingBlocks.Application;

namespace Persistence.MerchantRuntime.Vault;

/// <summary>
/// Adapts the narrow, applock-based <see cref="IVaultAuditAppender"/> (task 6's replacement for
/// <c>sec.usp_vault_audit_head</c>) onto the <see cref="IVaultRevealAuditWriter"/> port the vault store
/// injects (task 8's "1 principal" DI flip — this now IS the live implementation, superseding the
/// deleted proc-based <c>VaultRevealAuditWriter</c>). The one signature delta: the old port has no
/// <c>revealedAt</c> parameter, so this supplies <see cref="IClock.UtcNow"/> at the call boundary.
/// </summary>
internal sealed class VaultRevealAuditAppenderAdapter(IVaultAuditAppender appender, IClock clock) : IVaultRevealAuditWriter
{
    public Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken) =>
        appender.AppendAsync(merchantId, secretName, clock.UtcNow, cancellationToken);
}
