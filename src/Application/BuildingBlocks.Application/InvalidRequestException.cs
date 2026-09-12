namespace BuildingBlocks.Application;

/// <summary>400 request failure with a stable caller-safe machine code.</summary>
public sealed class InvalidRequestException : ArgumentException
{
    public string Code { get; }

    public InvalidRequestException(string message, string code) : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Invalid-request code is required.", nameof(code))
            : code;
    }
}
