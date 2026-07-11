namespace BuildingBlocks.Application;

/// <summary>
/// Appends one tamper-evident record that a secret was revealed (merchant, name, when — never the secret).
/// Called inside the vault reveal so the seam callers stay unchanged. The write is durable independently of
/// the caller's unit of work (a reveal that exposed plaintext to memory must be audited even if the caller
/// later rolls back), so the implementation persists on its own short-lived, merchant-bound scope.
/// </summary>
public interface IVaultRevealAuditWriter
{
    Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken);
}
