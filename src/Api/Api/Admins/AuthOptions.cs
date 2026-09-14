namespace Api.Admins;

/// <summary>
/// Admin console origins the host needs outside of authentication (the employee login itself is
/// <c>IdentityAccess</c>: Microsoft OIDC at the API, then a platform Bearer token). The section keeps its
/// <c>AdminSession</c> name so operator env keys (<c>AdminSession__WebAppBaseUrl</c>) are unchanged.
/// </summary>
internal sealed class AdminSessionOptions
{
    public const string SectionName = "AdminSession";

    /// <summary>Absolute origin of the admin SPA (e.g. <c>https://localhost:3001</c>). Blank = same-origin deploy
    /// behind one host. Operator config, never request input.</summary>
    public string WebAppBaseUrl { get; init; } = "";
    /// <summary>Absolute origin of the Development-only Scalar UI (e.g. <c>https://localhost:5001</c>).</summary>
    public string ScalarBaseUrl { get; init; } = "";
}
