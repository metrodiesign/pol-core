namespace BuildingBlocks.Application;

/// <summary>
/// Walks a tenant's reveal-audit hash chain and reports whether it is intact. Read-only, never decrypts
/// anything. Runs under a cross-tenant bypass principal (pol_admin) — the audit table is INSERT-only for
/// tenant principals, so only the bypass path can read it to verify. Exposed as an internal maintenance op
/// (not on the public API), never a tenant-facing endpoint.
/// </summary>
public interface IVaultRevealAuditVerifier
{
    Task<VaultAuditVerifyResult> VerifyAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Outcome of a chain verify. <see cref="FirstBrokenSeq"/> pinpoints the first tampered/missing row.</summary>
public sealed record VaultAuditVerifyResult(Guid TenantId, bool Ok, long? FirstBrokenSeq, string? Reason);
