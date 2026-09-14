using Accounts.Application;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace Api.IdentityAccess;

/// <summary>Registers the workforce SPA as an OpenIddict public client (authorization code + PKCE, refresh token,
/// revocation) at startup so employees can obtain platform JWTs without any confidential material in the browser.
/// The redirect URI is derived from <see cref="IdentityAccessOptions.WorkforceWebAppBaseUrl"/>.</summary>
internal sealed class WorkforceClientRegistration(
    IServiceScopeFactory scopes,
    IdentityAccessProviders providers,
    IOptions<IdentityAccessOptions> options,
    ILogger<WorkforceClientRegistration> logger) : IHostedService
{
    public const string CallbackPath = "/auth/callback";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Without the workforce Entra provider no employee can reach /oauth/authorize, so there is nothing to
        // register (and test hosts without a database boot without touching it).
        if (!providers.ContainsKey("employees"))
            return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var descriptor = Describe(options.Value);
            var existing = await applications.FindByClientIdAsync(descriptor.ClientId!, cancellationToken);
            if (existing is null)
                await applications.CreateAsync(descriptor, cancellationToken);
            else
                await applications.UpdateAsync(existing, descriptor, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Test hosts boot without a reachable database; a production failure surfaces as invalid_client at
            // /oauth/authorize and in this log line, never as a silent success.
            logger.LogError(exception, "Workforce OpenIddict client {ClientId} could not be registered.",
                options.Value.WorkforceClientId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static OpenIddictApplicationDescriptor Describe(IdentityAccessOptions settings)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ApplicationType = OpenIddictConstants.ApplicationTypes.Web,
            ClientId = settings.WorkforceClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            DisplayName = "Workforce web app",
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.Endpoints.Revocation,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
                OpenIddictConstants.Permissions.Prefixes.Resource + SystemClientScopeRegistry.ApiAudience,
            },
            Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange },
        };
        if (!string.IsNullOrEmpty(settings.WorkforceWebAppBaseUrl))
            descriptor.RedirectUris.Add(new Uri(settings.WorkforceWebAppBaseUrl.TrimEnd('/') + CallbackPath));
        return descriptor;
    }
}
