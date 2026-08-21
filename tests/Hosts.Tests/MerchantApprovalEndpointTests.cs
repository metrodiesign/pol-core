extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using Iam.Domain.Permissions;
using Mediator;
using Merchants.Application.GetMerchant;
using Merchants.Application.Users;
using Merchants.Domain.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AdminResolution = Admins.Application.Users.Resolution;
using MerchantUserStatus = Merchants.Domain.Users.UserStatus;

namespace Hosts.Tests;

// admin-merchant-provisioning-contract REQ-4.2-4.5: drive the real approve route, admin policy,
// permission gate and CSRF filter. IAdminQuery controls the already-scoped Merchant result while the recording
// mediator proves rejection short-circuits and success dispatches the validated Merchant id.

file sealed class ApprovalTestAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApprovalTestAdmin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "admin-sub-1")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class ApprovalBoundAdminScope : IAdminScope
{
    public bool IsBound => true;
    public AdminResolution Current { get; } = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "admin@example.com",
        Tier.Super,
        AccessibleMerchants.All)
    {
        Permissions = new HashSet<string> { Keys.MerchantUserApprove },
    };
    public AccessibleMerchants Accessible => AccessibleMerchants.All;
}

file sealed class StubApprovalAdminQuery(MerchantView? merchant) : ApiHost::Api.Admins.IAdminQuery
{
    public string? RequestedCode { get; private set; }

    public Task<MerchantView?> GetMerchantByCodeAsync(string code, CancellationToken cancellationToken)
    {
        RequestedCode = code;
        return Task.FromResult(merchant);
    }
}

file sealed class RecordingApproveMediator : IMediator
{
    public ApproveCommand? Command { get; private set; }

    private ValueTask<TResponse> Dispatch<TResponse>(object message)
    {
        if (message is not ApproveCommand command)
            throw new NotSupportedException($"Unexpected message '{message.GetType().Name}'.");

        Command = command;
        object result = new ApproveResult(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), MerchantUserStatus.Active, AlreadyActive: false);
        return ValueTask.FromResult((TResponse)result);
    }

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Dispatch<TResponse>(request);
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
        Dispatch<TResponse>(command);
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) =>
        Dispatch<TResponse>(query);
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
    public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => throw new NotSupportedException();
    public ValueTask Publish(object notification, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

file sealed class MerchantApprovalFactory(MerchantView? merchant) : WebApplicationFactory<ApiHost::Program>
{
    public RecordingApproveMediator Mediator { get; } = new();
    public StubApprovalAdminQuery AdminQuery { get; } = new(merchant);

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
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApprovalTestAdminAuthHandler>(
                ApprovalTestAdminAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(options => options.AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(ApprovalTestAdminAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            services.AddScoped<IAdminScope>(_ => new ApprovalBoundAdminScope());
            services.AddScoped<ApiHost::Api.Admins.IAdminQuery>(_ => AdminQuery);
            services.AddSingleton<IMediator>(Mediator);
        });
    }
}

public sealed class MerchantApprovalEndpointTests
{
    private static readonly Guid MerchantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid PendingUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly string Route = $"/api/v1/admins/merchants/users/{PendingUserId}/approve";

    [Fact]
    public async Task A_legacy_subject_value_in_the_route_is_404_at_the_guid_constraint()
    {
        // REQ-4.7/R1: the route contract is {merchantUserId:guid} — a non-GUID (old subject-style) value never
        // reaches the endpoint, so it cannot be misread as either a subject or an id.
        using var factory = new MerchantApprovalFactory(Merchant("Active"));
        using var client = factory.CreateClient();

        var request = ApproveRequest();
        request.RequestUri = new Uri("/api/v1/admins/merchants/users/google-sub-12345/approve", UriKind.Relative);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.Mediator.Command);
    }

    [Fact]
    public async Task Unknown_or_out_of_scope_merchant_returns_404_without_dispatching_approval()
    {
        using var factory = new MerchantApprovalFactory(merchant: null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(ApproveRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.Mediator.Command);
    }

    [Fact]
    public async Task Inactive_merchant_returns_409_without_dispatching_approval()
    {
        using var factory = new MerchantApprovalFactory(Merchant("Inactive"));
        using var client = factory.CreateClient();

        var response = await client.SendAsync(ApproveRequest());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(factory.Mediator.Command);
    }

    [Fact]
    public async Task Active_merchant_dispatches_approval_with_resolved_id_and_roles()
    {
        using var factory = new MerchantApprovalFactory(Merchant("Active"));
        using var client = factory.CreateClient();

        var response = await client.SendAsync(ApproveRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("vcommerce", factory.AdminQuery.RequestedCode);
        var command = Assert.IsType<ApproveCommand>(factory.Mediator.Command);
        Assert.Equal(MerchantId, command.ValidatedMerchantId);
        Assert.Equal(PendingUserId, command.MerchantUserId);
        Assert.Equal(["merchant_manager"], command.RoleCodes);
        Assert.Equal("admin:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", command.ActingAdminSubject);
        Assert.Equal(new ApprovalBoundAdminScope().Current.AdminId, command.ActingAdminId);
    }

    private static HttpRequestMessage ApproveRequest()
    {
        const string csrf = "approval-csrf";
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                """{"merchantCode":"vcommerce","roleCodes":["merchant_manager"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{cookieName}={csrf}");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, csrf);
        request.Headers.Add("If-Match", "\"v1\"");
        request.Headers.Add("Idempotency-Key", "approval-test-key");
        return request;
    }

    private static MerchantView Merchant(string status) => new(
        MerchantId,
        "vcommerce",
        "vCommerce Co., Ltd.",
        null,
        status,
        "TH",
        "THB",
        "card,promptpay",
        null,
        new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
        []);
}
