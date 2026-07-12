namespace Payments.Domain.Psp;

/// <summary>
/// The supported Payment Service Providers. The enum names follow the project naming rule
/// (acronyms >= 3 chars are PascalCase, so <c>2c2p</c> becomes <see cref="TwoCTwoP"/>), while the
/// stable wire/storage code strings are kept verbatim as the PSP publishes them ("2c2p"/"omise").
/// Use <see cref="Codes"/> to convert between the enum and its code string.
/// </summary>
public enum Code
{
    TwoCTwoP = 0,
    Omise = 1,
}

/// <summary>Helpers to map <see cref="Code"/> to and from its stable code string ("2c2p"/"omise").</summary>
public static class Codes
{
    /// <summary>The verbatim PSP code string for a <see cref="Code"/>.</summary>
    public static string ToCode(this Code psp) => psp switch
    {
        Code.TwoCTwoP => "2c2p",
        Code.Omise => "omise",
        _ => throw new ArgumentOutOfRangeException(nameof(psp), psp, "Unknown PSP code."),
    };

    /// <summary>Parses a stable PSP code string back to <see cref="Code"/>. Throws on unknown.</summary>
    public static Code FromCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code switch
        {
            "2c2p" => Code.TwoCTwoP,
            "omise" => Code.Omise,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown PSP code."),
        };
    }
}
