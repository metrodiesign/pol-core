using Accounts.Application;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace Api.IdentityAccess;

/// <summary>
/// Registers the two public OpenIddict clients (authorization code + PKCE) at startup: the workforce SPA
/// (<c>IdentityAccess:WorkforceClientId</c>, only when the "employees" provider is configured) and the agent SPA
/// (<c>IdentityAccess:AgentClientId</c>, only when the "agents" provider is configured). Both share one callback
/// path on their own origin; /oauth/authorize picks the OIDC provider from the client_id it receives.
/// </summary>
internal sealed class WorkforceClientRegistration(
    IServiceScopeFactory scopes,
    IdentityAccessProviders providers,
    IOptions<IdentityAccessOptions> options,
    ILogger<WorkforceClientRegistration> logger) : IHostedService
{
    public const string CallbackPath = "/auth/callback";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (providers.ContainsKey("employees"))
            await RegisterAsync(Describe(settings), cancellationToken);
        if (providers.ContainsKey("agents"))
            await RegisterAsync(DescribeAgent(settings), cancellationToken);
    }

    private async Task RegisterAsync(OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var existing = await applications.FindByClientIdAsync(descriptor.ClientId!, cancellationToken);
            if (existing is null)
                await applications.CreateAsync(descriptor, cancellationToken);
            else
                await applications.UpdateAsync(existing, descriptor, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "OpenIddict client {ClientId} could not be registered.", descriptor.ClientId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static OpenIddictApplicationDescriptor Describe(IdentityAccessOptions settings) =>
        Describe(settings.WorkforceClientId, "Workforce web app", settings.WorkforceWebAppBaseUrl);

    internal static OpenIddictApplicationDescriptor DescribeAgent(IdentityAccessOptions settings) =>
        Describe(settings.AgentClientId, "Agent web app", settings.AgentWebAppBaseUrl);

    private static OpenIddictApplicationDescriptor Describe(string clientId, string displayName, string webAppBaseUrl)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ApplicationType = OpenIddictConstants.ApplicationTypes.Web,
            ClientId = clientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            DisplayName = displayName,
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
        if (!string.IsNullOrEmpty(webAppBaseUrl))
            descriptor.RedirectUris.Add(new Uri(webAppBaseUrl.TrimEnd('/') + CallbackPath));
        return descriptor;
    }
}
