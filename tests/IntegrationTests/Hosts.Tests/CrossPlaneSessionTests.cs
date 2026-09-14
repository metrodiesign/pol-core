extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MerchantApp = Merchants.Application.Users;
using MerchantDomain = Merchants.Domain.Users;

namespace Hosts.Tests;

// microsoft-oidc-ciam-alignment REQ-6.4: the merchant-user session plane is isolated from the admin console. A
// merchant cookie that IS valid on its own plane (the fake store serves an Active session and the resolver answers
// an active account) is still 401 on the admin plane, whose only credential is the employee platform Bearer token.

file static class Tokens
{
    // "dummy-" prefix keeps the secret guard's placeholder allowlist happy — these are fixture values.
    public const string MerchantToken = "dummy-merchant-token";
    public static readonly Guid MerchantUserId = Guid.NewGuid();
    public static readonly Guid MerchantId = Guid.NewGuid();
    public static readonly MerchantDomain.SessionPolicy MerchantPolicy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
}

file sealed class FakeMerchantSessions : MerchantApp.ISessionStore
{
    public Task<MerchantDomain.Session?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken ct) =>
        Task.FromResult(tokenHash.SequenceEqual(ApiHost::Api.Merchants.UserTokens.Hash(Tokens.MerchantToken))
            ? MerchantDomain.Session.Start(Tokens.MerchantUserId, tokenHash, DateTime.UtcNow, Tokens.MerchantPolicy)
            : null);
    public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public void Add(MerchantDomain.Session session) { }
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
    public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) => Task.FromResult(false);
    public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
}

file sealed class FakeMerchantSessionResolver : ApiHost::Api.Merchants.IUserSessionResolver
{
    public Task<MerchantApp.ByIdResult> ResolveByIdAsync(Guid merchantUserId, CancellationToken ct) =>
        Task.FromResult(MerchantApp.ByIdResult.Of(
            new MerchantApp.Resolution(Tokens.MerchantUserId, "agent@example.com", Tokens.MerchantId,
                new HashSet<string>(StringComparer.Ordinal)),
            "merchant-sub-cross"));
}

file sealed class FakeMerchantRoles : Merchants.Application.Users.Roles.IRoleRepository
{
    public void AddAssignment(Merchants.Domain.Users.Roles.RoleAssignment assignment) => throw new NotSupportedException();
    public void RemoveAssignment(Merchants.Domain.Users.Roles.RoleAssignment assignment) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken ct) => throw new NotSupportedException();
    public Task<Merchants.Domain.Users.Roles.RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

file sealed class CrossPlaneFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddScoped<MerchantApp.ISessionStore, FakeMerchantSessions>();
            services.AddScoped<ApiHost::Api.Merchants.IUserSessionResolver, FakeMerchantSessionResolver>();
            services.AddScoped<Merchants.Application.Users.Roles.IRoleRepository, FakeMerchantRoles>();
        });
    }
}

public sealed class CrossPlaneSessionTests
{
    // Dev-http cookie names (the test client is plain http on the Development host).
    private const string MerchantCookie = ApiHost::Api.Merchants.UserSessionCookies.SessionCookieNameDevHttp;

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path, string cookieName, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{cookieName}={token}");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task The_merchant_cookie_authenticates_its_own_plane_and_401s_on_the_admin_plane()
    {
        using var factory = new CrossPlaneFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The cookie is VALID on its own plane first — otherwise the cross-plane 401 would prove nothing.
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/api/v1/merchants/users/me", MerchantCookie, Tokens.MerchantToken));

        // The merchant session cookie on an admin route: the admin console only reads a Bearer token -> 401.
        Assert.Equal(HttpStatusCode.Unauthorized,
            await GetAsync(client, "/api/v1/accounts", MerchantCookie, Tokens.MerchantToken));
    }
}
