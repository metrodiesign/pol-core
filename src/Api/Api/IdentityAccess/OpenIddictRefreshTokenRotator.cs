using OpenIddict.Abstractions;

namespace Api.IdentityAccess;

/// <summary>Rotates a reference refresh token through OpenIddict's token manager. The manager remains the
/// owner of token state; this adapter only redeems the old row and creates its successor.</summary>
internal sealed class OpenIddictRefreshTokenRotator(IOpenIddictTokenManager tokens)
{
    /// <summary>Issues the first reference refresh token of a BFF session. The upstream provider's refresh token
    /// is never stored: the platform session is refreshed and revoked through this OpenIddict row only.</summary>
    public async Task<string> IssueAsync(Guid accountId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var descriptor = new OpenIddictTokenDescriptor
        {
            CreationDate = DateTimeOffset.UtcNow,
            ExpirationDate = expiresAt,
            ReferenceId = BffSessionManager.NewToken(),
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = accountId.ToString("D"),
            Type = "refresh_token",
        };
        await tokens.CreateAsync(descriptor, cancellationToken);
        return descriptor.ReferenceId;
    }

    /// <summary>Redeems the current reference token and creates its successor expiring at <paramref name="expiresAt"/>:
    /// the successor must track the new BFF ticket, not the lifetime of the row it replaces, or a proactively
    /// refreshed session would outlive its refresh token row (and be pruned from under it once pruning is on).</summary>
    public async Task<string?> RotateAsync(string refreshToken, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return null;

        var current = await tokens.FindByReferenceIdAsync(refreshToken, cancellationToken);
        if (current is null || !await tokens.TryRedeemAsync(current, cancellationToken))
            return null;

        var replacement = new OpenIddictTokenDescriptor
        {
            ApplicationId = await tokens.GetApplicationIdAsync(current, cancellationToken),
            AuthorizationId = await tokens.GetAuthorizationIdAsync(current, cancellationToken),
            CreationDate = DateTimeOffset.UtcNow,
            ExpirationDate = expiresAt,
            ReferenceId = BffSessionManager.NewToken(),
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = await tokens.GetSubjectAsync(current, cancellationToken),
            Type = "refresh_token",
        };

        foreach (var property in await tokens.GetPropertiesAsync(current, cancellationToken))
            replacement.Properties[property.Key] = property.Value;

        await tokens.CreateAsync(replacement, cancellationToken);
        return replacement.ReferenceId;
    }
}
