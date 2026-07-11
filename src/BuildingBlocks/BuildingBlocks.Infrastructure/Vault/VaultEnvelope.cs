using System.Security.Cryptography;

namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// The envelope-encryption primitives shared by the vault store and the rotation maintenance op: a per-merchant
/// KEK derived from a master key (HKDF-SHA256) and AES-256-GCM seal/open over a packed nonce|ciphertext|tag.
/// The HKDF salt/info/algorithm are FROZEN — changing any of them would make every existing blob undecryptable.
/// </summary>
internal static class VaultEnvelope
{
    private static readonly byte[] KekInfo = "pol-core/vault/kek/v1"u8.ToArray();

    /// <summary>Derives the per-merchant KEK from a master key. Salt=merchantId, info fixed — must never change.</summary>
    public static byte[] DeriveKek(byte[] masterKey, Guid merchantId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, outputLength: 32, salt: merchantId.ToByteArray(), info: KekInfo);

    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var packed = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length + ciphertext.Length, tag.Length);
        return packed;
    }

    public static byte[] Decrypt(byte[] key, byte[] packed)
    {
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        var nonce = packed.AsSpan(0, nonceSize);
        var tag = packed.AsSpan(packed.Length - tagSize, tagSize);
        var ciphertext = packed.AsSpan(nonceSize, packed.Length - nonceSize - tagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
