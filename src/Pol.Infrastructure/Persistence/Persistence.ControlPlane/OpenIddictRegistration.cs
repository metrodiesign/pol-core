using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Application;
using Accounts.Application;
using Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Persistence.ControlPlane;

/// <summary>Registers one OpenIddict core/server/validation pipeline for the whole platform.</summary>
public static class OpenIddictRegistration
{
    public static IServiceCollection AddPlatformOAuth(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var issuer = configuration["OAuth:Issuer"];
        var openIddict = services.AddOpenIddict()
            .AddCore(options => options
                .UseEntityFrameworkCore()
                .UseDbContext<ControlPlaneDbContext>());

        openIddict.AddServer(options =>
        {
            options.SetTokenEndpointUris("/oauth/token")
                .SetAuthorizationEndpointUris("/oauth/authorize")
                .SetRevocationEndpointUris("/oauth/revoke")
                .SetConfigurationEndpointUris("/.well-known/oauth-authorization-server")
                .SetJsonWebKeySetEndpointUris("/.well-known/jwks.json")
                .AllowAuthorizationCodeFlow()
                .AllowClientCredentialsFlow()
                .AllowRefreshTokenFlow()
                .RequireProofKeyForCodeExchange()
                .SetAccessTokenLifetime(TimeSpan.FromMinutes(5))
                .UseAspNetCore()
                .EnableAuthorizationEndpointPassthrough()
                .EnableTokenEndpointPassthrough();

            if (!string.IsNullOrWhiteSpace(issuer))
                options.SetIssuer(issuer);

            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            {
                options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
            }
            else
            {
                var certificatePath = configuration["OAuth:CertificatePath"];
                if (string.IsNullOrWhiteSpace(certificatePath))
                    throw new InvalidOperationException(
                        "OAuth:CertificatePath is required outside Development/Testing; ephemeral signing keys are disabled.");
                options.AddSigningCertificate(certificatePath);
                options.AddEncryptionCertificate(certificatePath);
            }

            options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(descriptor => descriptor
                .UseScopedHandler<SystemClientTokenRequestHandler>()
                .SetOrder(100_000));
        });

        openIddict.AddValidation(options =>
        {
            options.UseLocalServer();
            options.UseAspNetCore();
            options.EnableTokenEntryValidation();
        });

        return services;
    }

    /// <summary>Applies OpenIddict's standard entities and keeps them in the target OAuth schema.</summary>
    public static void ApplyModel(ModelBuilder modelBuilder)
    {
        modelBuilder.UseOpenIddict();
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(type => type.Name.StartsWith("OpenIddict.", StringComparison.Ordinal)))
            entityType.SetSchema(SchemaNames.OAuth);
    }
}

/// <summary>Enforces Account/Client status, registered key policy and one-time assertion JTI at token exchange.</summary>
internal sealed class SystemClientTokenRequestHandler(
    IIdentityAccessQuery identities,
    SystemClientAssertionService assertionService,
    IClock clock) : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateTokenRequestContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateTokenRequestContext context)
    {
        var request = context.Request;
        if (request is null)
            return;

        if (string.Equals(request.GrantType, OpenIddictConstants.GrantTypes.RefreshToken,
                StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(request.ClientId))
        {
            var refreshClient = await identities.FindSystemClientAsync(
                request.ClientId, context.CancellationToken);
            if (refreshClient?.Account.AccountType == AccountType.System)
            {
                context.Reject(OpenIddictConstants.Errors.UnauthorizedClient,
                    "SYSTEM clients cannot use refresh tokens.");
            }
            return;
        }

        if (!string.Equals(request.GrantType, OpenIddictConstants.GrantTypes.ClientCredentials,
                StringComparison.Ordinal))
            return;

        if (string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ClientAssertion)
            || !string.Equals(
                request.ClientAssertionType,
                "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                StringComparison.Ordinal))
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "A private_key_jwt client assertion is required.");
            return;
        }

        var resolved = await identities.FindSystemClientAsync(request.ClientId, context.CancellationToken);
        if (resolved is null || resolved.Account.AccountType != AccountType.System)
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "The SYSTEM client is not registered.");
            return;
        }
        if (resolved.Account.Status != AccountStatus.Active)
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "The SYSTEM account is suspended.");
            return;
        }

        // This event runs after OpenIddict's ProcessAuthentication pipeline. OpenIddict has already verified the
        // assertion signature against the registered application's JsonWebKeySet and checked issuer/audience/
        // lifetime. The parse below only reads claims for our database policy; it never authenticates a signature.
        JsonWebToken token;
        try
        {
            token = new JsonWebTokenHandler().ReadJsonWebToken(request.ClientAssertion);
        }
        catch (Exception) when (!context.CancellationToken.IsCancellationRequested)
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client assertion is malformed.");
            return;
        }

        if (!token.TryGetPayloadValue<string>("jti", out var jti)
            || string.IsNullOrWhiteSpace(jti)
            || token.ValidFrom == DateTime.MinValue
            || token.ValidTo == DateTime.MinValue
            || string.IsNullOrWhiteSpace(token.Kid)
            || string.IsNullOrWhiteSpace(token.Alg))
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client assertion is incomplete.");
            return;
        }

        var key = resolved.KeyPolicies.FirstOrDefault(x =>
            string.Equals(x.KeyId, token.Kid, StringComparison.Ordinal));
        if (key is null)
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client assertion key is not registered.");
            return;
        }

        var validation = await assertionService.ValidateAsync(
            resolved.Account,
            resolved.Client,
            key,
            new ClientAssertion(
                request.ClientId,
                token.Issuer,
                token.Kid,
                token.Alg,
                jti,
                token.ValidFrom,
                token.ValidTo,
                token.Audiences.FirstOrDefault() ?? string.Empty),
            clock.UtcNow,
            context.Options.Issuer?.AbsoluteUri.TrimEnd('/') ?? "/oauth/token",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            context.CancellationToken);
        if (!validation.IsValid)
            context.Reject(OpenIddictConstants.Errors.InvalidClient, validation.Code);
    }
}
