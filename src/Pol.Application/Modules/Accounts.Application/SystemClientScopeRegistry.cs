namespace Accounts.Application;

/// <summary>One registry for SYSTEM OAuth scopes shared by management validation and OpenIddict server wiring.</summary>
public static class SystemClientScopeRegistry
{
    public const string ApiAudience = "pol-core-api";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "order.read",
        "order.write",
        "checkout.write",
        "transaction.read",
    };

    public static bool IsRegistered(string scope) => All.Contains(scope);
}
