namespace SharedKernel;

/// <summary>
/// A verified external identity: the provider slug ("microsoft"; historical rows may carry a retired slug) +
/// that provider's stable subject (Entra <c>oid</c>). Identity is always this PAIR — subjects are NOT unique across
/// providers (microsoft-oidc-ciam-alignment REQ-4.1), so no seam may accept a bare subject.
/// </summary>
public readonly record struct ProviderIdentity(string Provider, string Subject);
