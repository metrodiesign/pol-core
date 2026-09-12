using OpenIddict.Abstractions;

namespace Api.IdentityAccess;

/// <summary>Rotates a reference refresh token through OpenIddict's token manager. The manager remains the
/// owner of token state; this adapter only redeems the old row and creates its successor.</summary>
internal sealed class OpenIddictRefreshTokenRotator(IOpenIddictTokenManager tokens)
{
    public async Task<string?> RotateAsync(string refreshToken, CancellationToken cancellationToken)
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
            ExpirationDate = await tokens.GetExpirationDateAsync(current, cancellationToken),
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
