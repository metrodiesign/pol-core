extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

file sealed class MicrosoftInviteTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAdminScope scope)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "MicrosoftInviteTestAdmin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", "internal-admin"), new Claim("admin_tier", scope.Current.Tier.ToString())],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class MicrosoftInviteAdminScope(Tier tier) : IAdminScope
{
    public static readonly Guid AdminId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    public bool IsBound => true;
    public Resolution Current { get; } = new(AdminId, null, tier, AccessibleMerchants.All);
    public AccessibleMerchants Accessible => AccessibleMerchants.All;
}

file sealed class RecordingMicrosoftInviteMediator : IMediator
{
    public static readonly Guid CreatedAdminId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    public CreateScopedCommand? Command { get; private set; }

    private ValueTask<TResponse> Dispatch<TResponse>(object message)
    {
        Command = Assert.IsType<CreateScopedCommand>(message);
        object result = new CreateScopedResult(CreatedAdminId, null);
        return ValueTask.FromResult((TResponse)result);
    }

    public ValueTask<TResponse> Send<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default) => Dispatch<TResponse>(request);
    public ValueTask<TResponse> Send<TResponse>(
        ICommand<TResponse> command, CancellationToken cancellationToken = default) => Dispatch<TResponse>(command);
    public ValueTask<TResponse> Send<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken = default) => Dispatch<TResponse>(query);
    public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
        Dispatch<object?>(message);
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(
        object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask Publish<TNotification>(
        TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => throw new NotSupportedException();
    public ValueTask Publish(object notification, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

file sealed class MicrosoftInviteEndpointFactory(Tier tier) : WebApplicationFactory<ApiHost::Program>
{
    public RecordingMicrosoftInviteMediator Mediator { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
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
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MicrosoftInviteTestAuthHandler>(
                MicrosoftInviteTestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(options => options.AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(MicrosoftInviteTestAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));
            services.AddScoped<IAdminScope>(_ => new MicrosoftInviteAdminScope(tier));
            services.AddSingleton<IMediator>(Mediator);
        });
    }
}

public sealed class AdminMicrosoftInviteEndpointTests
{
    private static readonly Guid ObjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task Super_with_csrf_dispatches_the_verified_tuple_contract_and_returns_nullable_email()
    {
        using var factory = new MicrosoftInviteEndpointFactory(Tier.Super);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.SendAsync(Request(
            $$"""{"objectId":"{{ObjectId:D}}","identityApprovalReference":"entra-export-42"}"""));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/admins/{RecordingMicrosoftInviteMediator.CreatedAdminId:D}",
            response.Headers.Location?.ToString());
        var command = Assert.IsType<CreateScopedCommand>(factory.Mediator.Command);
        Assert.Equal(ObjectId, command.ObjectId);
        Assert.Null(command.Email);
        Assert.Equal("entra-export-42", command.IdentityApprovalReference);
        Assert.Equal(MicrosoftInviteAdminScope.AdminId, command.ActingAdminId);
        Assert.False(string.IsNullOrWhiteSpace(command.CorrelationId));

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(RecordingMicrosoftInviteMediator.CreatedAdminId,
            payload.RootElement.GetProperty("adminId").GetGuid());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("email").ValueKind);
    }

    [Fact]
    public async Task Malformed_object_id_is_400_before_dispatch()
    {
        using var factory = new MicrosoftInviteEndpointFactory(Tier.Super);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.SendAsync(Request(
            """{"objectId":"not-a-guid","identityApprovalReference":"entra-export-42"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Mediator.Command);
    }

    [Fact]
    public async Task Missing_csrf_is_403_before_dispatch()
    {
        using var factory = new MicrosoftInviteEndpointFactory(Tier.Super);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.SendAsync(Request(
            $$"""{"objectId":"{{ObjectId:D}}","identityApprovalReference":"entra-export-42"}""",
            includeCsrf: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(factory.Mediator.Command);
    }

    [Fact]
    public async Task Scoped_admin_is_403_before_dispatch()
    {
        using var factory = new MicrosoftInviteEndpointFactory(Tier.Scoped);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.SendAsync(Request(
            $$"""{"objectId":"{{ObjectId:D}}","identityApprovalReference":"entra-export-42"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(factory.Mediator.Command);
    }

    private static HttpRequestMessage Request(string body, bool includeCsrf = true)
    {
        const string csrf = "microsoft-invite-csrf";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admins")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (includeCsrf)
        {
            var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
            request.Headers.Add("Cookie", $"{cookieName}={csrf}");
            request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, csrf);
        }
        return request;
    }
}
