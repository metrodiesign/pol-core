namespace Payments.Domain;

/// <summary>
/// The supported Payment Service Providers. The enum names follow the project naming rule
/// (acronyms >= 3 chars are PascalCase, so <c>2c2p</c> becomes <see cref="TwoCTwoP"/>), while the
/// stable wire/storage code strings are kept verbatim as the PSP publishes them ("2c2p"/"omise").
/// Use <see cref="PspCodes"/> to convert between the enum and its code string.
/// </summary>
public enum PspCode
{
    TwoCTwoP = 0,
    Omise = 1,
}

/// <summary>Helpers to map <see cref="PspCode"/> to and from its stable code string ("2c2p"/"omise").</summary>
public static class PspCodes
{
    /// <summary>The verbatim PSP code string for a <see cref="PspCode"/>.</summary>
    public static string ToCode(this PspCode psp) => psp switch
    {
        PspCode.TwoCTwoP => "2c2p",
        PspCode.Omise => "omise",
        _ => throw new ArgumentOutOfRangeException(nameof(psp), psp, "Unknown PSP code."),
    };

    /// <summary>Parses a stable PSP code string back to <see cref="PspCode"/>. Throws on unknown.</summary>
    public static PspCode FromCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code switch
        {
            "2c2p" => PspCode.TwoCTwoP,
            "omise" => PspCode.Omise,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown PSP code."),
        };
    }
}
