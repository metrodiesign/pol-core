using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// One append-only, hash-chained record of the FACT that a secret was revealed — tenant, secret name, and
/// when, NEVER the secret/plaintext/DEK/KEK/hint. Each row folds in the previous row's hash (per-tenant
/// chain), so editing or deleting any row breaks every later row's recomputed hash and the per-tenant
/// <see cref="Seq"/> gap exposes a deletion. The hash is computed in the factory so a caller can never
/// persist a forged hash; the type has no mutators (append-only by construction).
/// </summary>
public sealed class VaultRevealAudit
{
    public long Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string SecretName { get; private set; } = default!;
    public long Seq { get; private set; }
    public byte[] PrevHash { get; private set; } = default!;
    public byte[] Hash { get; private set; } = default!;
    public DateTime RevealedAtUtc { get; private set; }

    private VaultRevealAudit() { }

    /// <summary>The chain anchor: 32 zero bytes is the PrevHash of a tenant's first (Seq=1) row.</summary>
    public static byte[] Genesis => new byte[32];

    public static VaultRevealAudit Append(byte[] prevHash, Guid tenantId, string secretName, long seq, DateTime utcNow) =>
        new()
        {
            TenantId = tenantId,
            SecretName = secretName,
            Seq = seq,
            PrevHash = prevHash,
            RevealedAtUtc = utcNow,
            Hash = ComputeHash(prevHash, tenantId, secretName, seq, utcNow),
        };

    /// <summary>The ONE canonical chain-hash layout (length-prefixing the name removes concatenation
    /// ambiguity). Append and verify both call this so they agree by construction.</summary>
    public static byte[] ComputeHash(byte[] prevHash, Guid tenantId, string secretName, long seq, DateTime utcNow)
    {
        var name = Encoding.UTF8.GetBytes(secretName);
        var buffer = new byte[32 + 16 + 4 + name.Length + 8 + 8];
        var span = buffer.AsSpan();

        prevHash.CopyTo(span);                                                 // 32
        tenantId.TryWriteBytes(span.Slice(32, 16));                            // 16
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(48, 4), name.Length);
        name.CopyTo(span.Slice(52, name.Length));
        var p = 52 + name.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(p, 8), utcNow.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(p + 8, 8), seq);

        return SHA256.HashData(buffer);
    }
}
