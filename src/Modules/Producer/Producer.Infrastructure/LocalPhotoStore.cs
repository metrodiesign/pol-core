using System.Text.RegularExpressions;
using Producer.Application;

namespace Producer.Infrastructure;

/// <summary>
/// Dev/local <see cref="IPhotoStore"/>: writes photo bytes to a gitignored directory under a server-generated,
/// opaque object key (a GUID + a type extension — NEVER the client filename, REQ-7.2/7.5), so a crafted name
/// cannot traverse the store. The canonical content-type is encoded in the key's extension and recovered on read.
/// Prod swaps an S3/Blob adapter behind the same port. The bytes are validated (type/magic-byte/size) before they
/// ever reach here (REQ-7.3/7.4).
/// </summary>
public sealed partial class LocalPhotoStore : IPhotoStore
{
    private readonly string _root;

    public LocalPhotoStore(string rootPath)
    {
        _root = rootPath;
        Directory.CreateDirectory(_root);
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
