extern alias ApiHost;
using System.Net;
using Mediator;
using Merchants.Application.Users;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Hosts.Tests;

// POST /api/v1/merchants/auth/invitations/start accepts a form `provider` slug (REQ-5.1-5.5): default google,
// normalized lowercase, restricted to the verified-email allowlist (currently google ONLY — Entra email is a
// mutable claim, so invitation-by-email matching would be a privilege-escalation hole, B3). microsoft — even
// fully configured — and unconfigured providers both 404 like the login endpoint.

file sealed class InvitationStartFactory : WebApplicationFactory<ApiHost::Program>
{
    public static readonly Guid InvitationId = Guid.NewGuid();
    public const string Tenant = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("MerchantAuth:Providers:Google:ClientId", "invite-client.apps.googleusercontent.com");
        builder.UseSetting("MerchantAuth:Providers:Google:ClientSecret", "test-secret");
        builder.UseSetting("MerchantAuth:Providers:Google:CallbackPath", "/api/v1/merchants/auth/google/callback");
        // Microsoft is fully CONFIGURED on purpose: 5.5 pins that configuration alone does not open the
        // invitation flow — only verified-email providers may start one.
        builder.UseSetting("MerchantAuth:Providers:Microsoft:Authority", $"https://login.microsoftonline.com/{Tenant}/v2.0");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", "22222222-bbbb-bbbb-bbbb-222222222222");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:CallbackPath", "/api/v1/merchants/auth/microsoft/callback");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddSingleton<IMediator>(new InvitationAnsweringMediator());
            foreach (var scheme in new[]
            {
                ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Google",
                ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Microsoft",
            })
                services.PostConfigure<OpenIdConnectOptions>(scheme, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = "https://test-issuer.example.com",
                            AuthorizationEndpoint = "https://idp.example.com/authorize",
                            TokenEndpoint = "https://idp.example.com/token",
                            JwksUri = "https://idp.example.com/keys",
                        }));
        });
    }
}

public sealed class InvitationStartProviderTests
{
    private const string Route = "/api/v1/merchants/auth/invitations/start";

    [Theory]
    [InlineData("google")]   // REQ-5.1: explicit verified-email provider
    [InlineData(null)]       // REQ-5.2: omitted -> default google
    [InlineData("GOOGLE")]   // REQ-5.1/L9: slug is case-normalized before lookup
    public async Task A_verified_email_provider_starts_the_challenge(string? provider)
    {
        using var factory = new InvitationStartFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync(Route, Form(provider));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("idp.example.com", response.Headers.Location!.Host);
    }

    [Theory]
    [InlineData("facebook")]   // REQ-5.3: not configured at all
    [InlineData("microsoft")]  // REQ-5.5: configured but NOT verified-email -> still 404
    [InlineData("Microsoft")]
    public async Task A_non_verified_email_or_unconfigured_provider_is_404(string provider)
    {
        using var factory = new InvitationStartFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync(Route, Form(provider));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static FormUrlEncodedContent Form(string? provider)
    {
        var pairs = new List<KeyValuePair<string, string>> { new("token", "a-wire-invitation-token") };
        if (provider is not null)
            pairs.Add(new("provider", provider));
        return new FormUrlEncodedContent(pairs);
    }
}

file sealed class InvitationAnsweringMediator : IMediator
{
    private ValueTask<T> Answer<T>(object message)
    {
        object? answer = message is ResolveInvitationTokenQuery
            ? new InvitationResolution(InvitationStartFactory.InvitationId, Guid.NewGuid(),
                "invited@example.com", "invited@example.com")
            : null;
        return new ValueTask<T>((T)answer!);
    }

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => Answer<TResponse>(request);
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default) => Answer<TResponse>(command);
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken ct = default) => Answer<TResponse>(query);
    public ValueTask<object?> Send(object message, CancellationToken ct = default) => Answer<object?>(message);

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask Publish<TNotification>(TNotification n, CancellationToken ct = default) where TNotification : INotification => throw new NotSupportedException();
    public ValueTask Publish(object n, CancellationToken ct = default) => throw new NotSupportedException();
}
