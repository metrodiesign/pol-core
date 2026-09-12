using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Governance.Application;

namespace Persistence.ControlPlane.Governance;

internal sealed record AuditAnchorOptions(string Path, string SigningKeyFile);

internal sealed class DisabledAuditAnchorStore : IAuditAnchorStore
{
    public bool IsEnabled => false;

    public Task<IReadOnlyDictionary<string, AuditAnchorCheckpoint>> ReadAllLatestAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, AuditAnchorCheckpoint>>(
            new Dictionary<string, AuditAnchorCheckpoint>(StringComparer.Ordinal));

    public Task AppendAsync(AuditAnchorCheckpoint checkpoint, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class FileAuditAnchorStore : IAuditAnchorStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly AuditAnchorOptions _options;
    private readonly byte[] _signingKey;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAuditAnchorStore(AuditAnchorOptions options)
    {
        _options = options;
        _signingKey = LoadSigningKey(options.SigningKeyFile);
    }

    public bool IsEnabled => true;

    public async Task<IReadOnlyDictionary<string, AuditAnchorCheckpoint>> ReadAllLatestAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_options.Path))
                return new Dictionary<string, AuditAnchorCheckpoint>(StringComparer.Ordinal);
            await using var stream = OpenRead(_options.Path);
            return (await ReadValidatedAsync(stream, cancellationToken)).Latest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(AuditAnchorCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ValidateCheckpoint(checkpoint);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_options.Path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new InvalidOperationException("Audit anchor directory is unavailable.");

            await using var stream = new FileStream(
                _options.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var state = await ReadValidatedAsync(stream, cancellationToken);
            if (state.Latest.TryGetValue(checkpoint.ScopeKey, out var current))
            {
                if (checkpoint.Sequence < current.Sequence)
                    throw new AuditIntegrityException("Audit anchor sequence regressed.");
                if (checkpoint.Sequence == current.Sequence)
                {
                    if (!string.Equals(checkpoint.Hash, current.Hash, StringComparison.Ordinal))
                        throw new AuditIntegrityException("Audit anchor hash changed at an anchored sequence.");
                    return;
                }
            }

            var persisted = Sign(checkpoint, state.LastSignature);
            var line = JsonSerializer.Serialize(persisted, JsonOptions) + "\n";
            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_signingKey);
        _gate.Dispose();
    }

    private static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task<AnchorFileState> ReadValidatedAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        var latest = new Dictionary<string, AuditAnchorCheckpoint>(StringComparer.Ordinal);
        var previousSignature = "";
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new AuditIntegrityException("Audit anchor file contains an empty record.");
            PersistedAuditAnchor persisted;
            try
            {
                persisted = JsonSerializer.Deserialize<PersistedAuditAnchor>(line, JsonOptions)
                    ?? throw new JsonException("Anchor record is null.");
            }
            catch (JsonException ex)
            {
                throw new AuditIntegrityException($"Audit anchor record is malformed: {ex.GetType().Name}.");
            }

            var checkpoint = new AuditAnchorCheckpoint(
                persisted.ScopeKey, persisted.Sequence, persisted.Hash, persisted.AnchoredAt);
            ValidateCheckpoint(checkpoint);
            if (!string.Equals(persisted.PreviousSignature, previousSignature, StringComparison.Ordinal))
                throw new AuditIntegrityException("Audit anchor signature chain is broken.");
            VerifySignature(persisted);
            if (latest.TryGetValue(checkpoint.ScopeKey, out var prior)
                && checkpoint.Sequence <= prior.Sequence)
                throw new AuditIntegrityException("Audit anchor scope sequence is not increasing.");
            latest[checkpoint.ScopeKey] = checkpoint;
            previousSignature = persisted.Signature;
        }
        return new AnchorFileState(latest, previousSignature);
    }

    private PersistedAuditAnchor Sign(AuditAnchorCheckpoint checkpoint, string previousSignature)
    {
        var canonical = Canonical(checkpoint, previousSignature);
        var signature = Convert.ToBase64String(
            HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(canonical)));
        return new PersistedAuditAnchor(
            checkpoint.ScopeKey,
            checkpoint.Sequence,
            checkpoint.Hash,
            checkpoint.AnchoredAt.ToUniversalTime(),
            previousSignature,
            signature);
    }

    private void VerifySignature(PersistedAuditAnchor persisted)
    {
        byte[] actual;
        try
        {
            actual = Convert.FromBase64String(persisted.Signature);
        }
        catch (FormatException)
        {
            throw new AuditIntegrityException("Audit anchor signature is malformed.");
        }
        var checkpoint = new AuditAnchorCheckpoint(
            persisted.ScopeKey, persisted.Sequence, persisted.Hash, persisted.AnchoredAt);
        var expected = HMACSHA256.HashData(
            _signingKey, Encoding.UTF8.GetBytes(Canonical(checkpoint, persisted.PreviousSignature)));
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new AuditIntegrityException("Audit anchor signature verification failed.");
    }

    private static string Canonical(AuditAnchorCheckpoint checkpoint, string previousSignature) =>
        string.Join('\n',
            checkpoint.ScopeKey,
            checkpoint.Sequence.ToString(CultureInfo.InvariantCulture),
            checkpoint.Hash,
            checkpoint.AnchoredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            previousSignature);

    private static void ValidateCheckpoint(AuditAnchorCheckpoint checkpoint)
    {
        var validScope = checkpoint.ScopeKey == "platform"
            || (checkpoint.ScopeKey.StartsWith("merchant:", StringComparison.Ordinal)
                && Guid.TryParseExact(checkpoint.ScopeKey[9..], "D", out var merchantId)
                && merchantId != Guid.Empty);
        if (!validScope || checkpoint.Sequence < 1
            || checkpoint.Hash.Length != 64 || checkpoint.Hash.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c))
            || checkpoint.AnchoredAt.Kind != DateTimeKind.Utc)
            throw new AuditIntegrityException("Audit anchor checkpoint is invalid.");
    }

    private static byte[] LoadSigningKey(string path)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new InvalidOperationException("Audit anchor signing key file is unavailable or malformed.", ex);
        }
        if (key.Length != 32 || key.All(b => b == 0))
            throw new InvalidOperationException("Audit anchor signing key must be a non-zero 32-byte base64 value.");
        return key;
    }

    private sealed record PersistedAuditAnchor(
        string ScopeKey,
        long Sequence,
        string Hash,
        DateTime AnchoredAt,
        string PreviousSignature,
        string Signature);

    private sealed record AnchorFileState(
        IReadOnlyDictionary<string, AuditAnchorCheckpoint> Latest,
        string LastSignature);
}
