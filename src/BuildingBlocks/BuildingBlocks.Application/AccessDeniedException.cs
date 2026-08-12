namespace BuildingBlocks.Application;

public sealed class AccessDeniedException(string message, string code = "permission_denied")
    : Exception(message)
{
    public string Code { get; } = code;
}
