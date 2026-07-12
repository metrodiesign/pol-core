using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Application.Users.Permissions;

namespace Merchants.Tests;

/// <summary>Photo upload validation (REQ-7.3/7.4/7.5): an allowlisted content-type whose ACTUAL magic bytes confirm
/// the same type is accepted (and the canonical stored type is the sniffed one); a header that lies about the bytes,
/// an excluded type (SVG), an oversize, or an empty upload is rejected and nothing is stored.</summary>
public sealed class PhotoValidationTests
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] WebpBytes = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/jpg")]   // common alias normalises to image/jpeg
    [InlineData("IMAGE/JPEG")]  // case-insensitive
    public void Jpeg_with_matching_magic_bytes_is_accepted_as_image_jpeg(string declared)
    {
        var result = PhotoValidation.Validate(declared, JpegBytes, JpegBytes.Length);
        Assert.True(result.IsValid);
        Assert.Equal(PhotoValidation.Jpeg, result.ContentType);
    }

    [Fact]
    public void Png_with_matching_magic_bytes_is_accepted()
    {
        var result = PhotoValidation.Validate("image/png", PngBytes, PngBytes.Length);
        Assert.True(result.IsValid);
        Assert.Equal(PhotoValidation.Png, result.ContentType);
    }

    [Fact]
    public void Webp_with_matching_magic_bytes_is_accepted()
    {
        var result = PhotoValidation.Validate("image/webp", WebpBytes, WebpBytes.Length);
        Assert.True(result.IsValid);
        Assert.Equal(PhotoValidation.Webp, result.ContentType);
    }

    [Theory]
    [InlineData("image/svg+xml")] // SVG excluded (script-bearing)
    [InlineData("image/gif")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public void A_non_allowlisted_declared_type_is_rejected(string? declared)
    {
        var result = PhotoValidation.Validate(declared, JpegBytes, JpegBytes.Length);
        Assert.False(result.IsValid);
        Assert.Null(result.ContentType);
    }

    [Fact]
    public void A_header_that_lies_about_the_bytes_is_rejected()
    {
        // Declared png but the bytes are jpeg — the magic-byte sniff must override the declared header (REQ-7.3).
        var result = PhotoValidation.Validate("image/png", JpegBytes, JpegBytes.Length);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Unrecognised_magic_bytes_are_rejected_even_with_an_allowlisted_header()
    {
        byte[] notAnImage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        var result = PhotoValidation.Validate("image/jpeg", notAnImage, notAnImage.Length);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void An_oversize_photo_is_rejected()
    {
        var result = PhotoValidation.Validate("image/jpeg", JpegBytes, length: 2_000_001, maxBytes: 2_000_000);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void An_empty_photo_is_rejected()
    {
        var result = PhotoValidation.Validate("image/jpeg", [], length: 0);
        Assert.False(result.IsValid);
    }
}
