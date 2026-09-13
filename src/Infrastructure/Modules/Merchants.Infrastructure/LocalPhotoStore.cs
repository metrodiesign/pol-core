using System.Text.RegularExpressions;
using Merchants.Application;
using Merchants.Application.Users;

namespace Merchants.Infrastructure;

/// <summary>
/// Dev/local <see cref="IPhotoStore"/>: writes photo bytes to a gitignored directory under a server-generated,
/// opaque object key (a GUID + a type extension — NEVER the client filename, REQ-7.2/7.5), so a crafted name
/// cannot traverse the store. The canonical content-type is encoded in the key's extension and recovered on read.
/// Prod swaps an S3/Blob adapter behind the same port. The bytes are validated (type/magic-byte/size) before they
/// ever reach here (REQ-7.3/7.4).
/// </summary>
public sealed partial class LocalPhotoStore : IPhotoStore
{
    private const int OperationLockCount = 256;
    public static readonly TimeSpan StagingTtl = TimeSpan.FromHours(24);

    private readonly string _root;
    private readonly string _stagingRoot;
    private readonly SemaphoreSlim[] _operationLocks =
        [.. Enumerable.Range(0, OperationLockCount).Select(static _ => new SemaphoreSlim(1, 1))];

    public LocalPhotoStore(string rootPath)
    {
        _root = rootPath;
        Directory.CreateDirectory(_root);
        _stagingRoot = Path.Combine(_root, ".staged");
        Directory.CreateDirectory(_stagingRoot);
    }

    public async Task<string> PutAsync(byte[] bytes, string contentType, CancellationToken cancellationToken)
    {
        var key = $"{Guid.NewGuid():N}{ExtensionFor(contentType)}";
        await File.WriteAllBytesAsync(Path.Combine(_root, key), bytes, cancellationToken).ConfigureAwait(false);
        return key;
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(string objectKey, CancellationToken cancellationToken)
    {
        // The key is our own opaque token; reject anything that is not the exact shape we mint so a crafted key
        // (path separators, traversal) can never reach the filesystem (REQ-7.5).
        if (!KeyPattern().IsMatch(objectKey))
            return null;
        var path = Path.Combine(_root, objectKey);
        if (!File.Exists(path))
            return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return (bytes, ContentTypeFor(Path.GetExtension(objectKey)));
    }

    public async Task<(string Key, bool CreatedNew)> PutStagedAsync(
        Guid operationId,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation id is required.", nameof(operationId));
        if (bytes.IsEmpty)
            throw new ArgumentException("Photo bytes are required.", nameof(bytes));

        try
        {
            DeleteExpiredStagedObjects(DateTime.UtcNow);

            var key = $"{operationId:N}{ExtensionFor(contentType)}";
            var operationLock = _operationLocks[operationId.GetHashCode() & (OperationLockCount - 1)];
            await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existingKey = ExistingKeyFor(operationId);
                if (existingKey is not null)
                {
                    if (!string.Equals(existingKey, key, StringComparison.Ordinal))
                        throw new InvalidOperationException("The KYC operation already contains a different photo type.");

                    var existingPath = File.Exists(CommittedPath(key)) ? CommittedPath(key) : StagedPath(key);
                    var existing = await File.ReadAllBytesAsync(existingPath, cancellationToken).ConfigureAwait(false);
                    if (!bytes.Span.SequenceEqual(existing))
                        throw new InvalidOperationException("The KYC operation already contains different photo content.");
                    return (key, false);
                }

                await File.WriteAllBytesAsync(StagedPath(key), bytes.ToArray(), cancellationToken).ConfigureAwait(false);
                return (key, true);
            }
            finally
            {
                operationLock.Release();
            }
        }
        catch (IOException)
        {
            throw new InvalidOperationException("KYC photo staging failed.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("KYC photo staging failed.");
        }
    }

    public Task CommitAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stagedPath = StagedPath(objectKey);
        var committedPath = CommittedPath(objectKey);

        if (File.Exists(committedPath))
        {
            File.Delete(stagedPath);
            return Task.CompletedTask;
        }

        if (!File.Exists(stagedPath))
            return Task.CompletedTask;

        try
        {
            File.Move(stagedPath, committedPath);
        }
        catch (IOException) when (File.Exists(committedPath))
        {
            File.Delete(stagedPath);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(StagedPath(objectKey));
        File.Delete(CommittedPath(objectKey));
        return Task.CompletedTask;
    }

    public Task DiscardStagedAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(StagedPath(objectKey));
        return Task.CompletedTask;
    }

    /// <summary>Sweeps staged objects past <see cref="StagingTtl"/>, independent of new upload traffic — the sweep
    /// inside <see cref="PutStagedAsync"/> only runs when a new staging call happens, which does not hold the
    /// advertised TTL bound after a crash with no further uploads. Callable on a timer (see
    /// Api.Merchants.PhotoStagingPruneService).</summary>
    public Task PruneExpiredStagedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteExpiredStagedObjects(DateTime.UtcNow);
        return Task.CompletedTask;
    }

    private void DeleteExpiredStagedObjects(DateTime now)
    {
        var cutoff = now - StagingTtl;
        foreach (var path in Directory.EnumerateFiles(_stagingRoot))
        {
            if (File.GetLastWriteTimeUtc(path) <= cutoff)
                File.Delete(path);
        }
    }

    private string StagedPath(string objectKey) => Path.Combine(_stagingRoot, ValidatedKey(objectKey));
    private string CommittedPath(string objectKey) => Path.Combine(_root, ValidatedKey(objectKey));

    private string? ExistingKeyFor(Guid operationId)
    {
        var prefix = operationId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var extension in new[] { ".jpg", ".png", ".webp" })
        {
            var candidate = prefix + extension;
            if (File.Exists(CommittedPath(candidate)) || File.Exists(StagedPath(candidate)))
                return candidate;
        }

        return null;
    }

    private static string ValidatedKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || !KeyPattern().IsMatch(objectKey))
            throw new ArgumentException("Invalid photo object key.", nameof(objectKey));
        return objectKey;
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        PhotoValidation.Jpeg => ".jpg",
        PhotoValidation.Png => ".png",
        PhotoValidation.Webp => ".webp",
        _ => throw new ArgumentException($"Unsupported photo content-type '{contentType}'.", nameof(contentType)),
    };

    private static string ContentTypeFor(string extension) => extension switch
    {
        ".jpg" => PhotoValidation.Jpeg,
        ".png" => PhotoValidation.Png,
        ".webp" => PhotoValidation.Webp,
        _ => "application/octet-stream",
    };

    [GeneratedRegex("^[0-9a-f]{32}\\.(jpg|png|webp)$")]
    private static partial Regex KeyPattern();
}
