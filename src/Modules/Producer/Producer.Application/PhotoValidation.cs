namespace Producer.Application;

/// <summary>Outcome of validating an uploaded profile photo (REQ-7.3/7.4). On success carries the canonical
/// content-type to STORE (derived from the verified magic bytes, never the client's declared header — REQ-7.5).</summary>
public sealed record PhotoValidationResult(bool IsValid, string? ContentType, string? Error)
{
    public static PhotoValidationResult Ok(string contentType) => new(true, contentType, null);
    public static PhotoValidationResult Reject(string error) => new(false, null, error);
}

/// <summary>
/// Pure validation for an uploaded profile photo (REQ-7.3/7.4/7.5). The content-type that gets stored is decided by
/// a magic-byte sniff of the ACTUAL bytes, not the declared header — a lie in the header cannot smuggle a different
/// type past this. SVG is excluded (only raster jpeg/png/webp) so no script-bearing image is ever stored. Size is
/// the caller's responsibility to bound BEFORE buffering (the endpoint caps the request body, REQ-7.4/N3); this
/// also re-checks the buffered length as defence in depth.
/// </summary>
public static class PhotoValidation
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";

    /// <summary>The default cap (2 MB, REQ-7.4); the endpoint reads it from configuration and bounds the body first.</summary>
    public const long DefaultMaxBytes = 2 * 1024 * 1024;

    /// <summary>Validates a photo from its declared content-type, its leading bytes, and its total length. The
    /// declared type must be allowlisted AND the magic bytes must independently confirm the same type; otherwise it
    /// is rejected and nothing is stored. Returns the canonical content-type to persist (REQ-7.5).</summary>
    public static PhotoValidationResult Validate(string? declaredContentType, ReadOnlySpan<byte> header, long length,
        long maxBytes = DefaultMaxBytes)
    {
        if (length <= 0)
            return PhotoValidationResult.Reject("The photo is empty.");
        if (length > maxBytes)
            return PhotoValidationResult.Reject($"The photo exceeds the {maxBytes}-byte limit.");

        var declared = Normalize(declaredContentType);
        if (declared is null)
            return PhotoValidationResult.Reject("The photo content-type is not allowed (jpeg, png, webp only).");

        var sniffed = Sniff(header);
        if (sniffed is null)
            return PhotoValidationResult.Reject("The photo bytes are not a recognised jpeg, png, or webp image.");

        // The header may not lie about the type: declared and sniffed must agree. Store the sniffed (true) type.
        if (!string.Equals(declared, sniffed, StringComparison.Ordinal))
            return PhotoValidationResult.Reject("The photo content-type does not match its actual bytes.");

        return PhotoValidationResult.Ok(sniffed);
    }

    /// <summary>The allowlisted content-type in canonical form, or null. <c>image/jpg</c> is accepted as a common
    /// alias of <c>image/jpeg</c>; SVG and everything else is rejected.</summary>
    private static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;
        // Drop any "; charset=..." parameter and case/space noise.
        var value = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return value switch
        {
            "image/jpeg" or "image/jpg" => Jpeg,
            "image/png" => Png,
            "image/webp" => Webp,
            _ => null,
        };
    }

    /// <summary>Identifies the image type from its leading magic bytes (REQ-7.3), or null if unrecognised.</summary>
    private static string? Sniff(ReadOnlySpan<byte> b)
    {
        // JPEG: FF D8 FF
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return Jpeg;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
            b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return Png;
        // WEBP: "RIFF" .... "WEBP"
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
            return Webp;
        return null;
    }
}
