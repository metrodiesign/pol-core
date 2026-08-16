extern alias ApiHost;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AdminUsers = Admins.Application.Users;
using AdminDomain = Admins.Domain.Users;
using MerchantApp = Merchants.Application.Users;
using MerchantDomain = Merchants.Domain.Users;

namespace Hosts.Tests;

// microsoft-oidc-ciam-alignment REQ-6.4: the two session planes are fully isolated. A cookie that IS valid on
// its own plane (both fake stores serve an Active session and the resolvers answer an active account) must
// still be 401 on the OTHER plane in both directions — each scheme reads only its own cookie name, and each
// policy pins only its own scheme.

file static class Tokens
{
    // "dummy-" prefix keeps the secret guard's placeholder allowlist happy — these are fixture values.
    public const string AdminToken = "dummy-admin-token";
    public const string MerchantToken = "dummy-merchant-token";
    public static readonly Guid AdminId = Guid.NewGuid();
    public static readonly Guid MerchantUserId = Guid.NewGuid();
    public static readonly Guid MerchantId = Guid.NewGuid();
    public static readonly MerchantDomain.SessionPolicy MerchantPolicy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
    public static readonly AdminDomain.SessionPolicy AdminPolicy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
}

file sealed class FakeAdminSessions : AdminUsers.ISessionStore
{
    public Task<AdminDomain.Session?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken ct) =>
        Task.FromResult(tokenHash.SequenceEqual(ApiHost::Api.Admins.SessionTokens.Hash(Tokens.AdminToken))
            ? AdminDomain.Session.Start(Tokens.AdminId, tokenHash, DateTime.UtcNow, Tokens.AdminPolicy)
            : null);
    public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public void Add(AdminDomain.Session session) { }
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
    public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) => Task.FromResult(false);
    public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeAllForAdminAsync(Guid adminId, CancellationToken ct) => Task.CompletedTask;
    public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
    public Task<IReadOnlyList<AdminDomain.Session>> ListByAdminAsync(Guid adminAccountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AdminDomain.Session>>([]);
    public Task<AdminDomain.Session?> FindByIdAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult<AdminDomain.Session?>(null);
}

file sealed class FakeAdminSessionResolver : ApiHost::Api.Admins.ISessionResolver
{
    public Task<AdminUsers.ByIdResult> ResolveByIdAsync(Guid adminAccountId, CancellationToken ct) =>
        Task.FromResult(AdminUsers.ByIdResult.Of(
            new AdminUsers.Resolution(Tokens.AdminId, "ops@example.com", AdminDomain.Tier.Super,
                AdminUsers.AccessibleMerchants.All),
            "admin-sub-cross"));
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
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddScoped<AdminUsers.ISessionStore, FakeAdminSessions>();
            services.AddScoped<ApiHost::Api.Admins.ISessionResolver, FakeAdminSessionResolver>();
            services.AddScoped<MerchantApp.ISessionStore, FakeMerchantSessions>();
            services.AddScoped<ApiHost::Api.Merchants.IUserSessionResolver, FakeMerchantSessionResolver>();
            services.AddScoped<Merchants.Application.Users.Roles.IRoleRepository, FakeMerchantRoles>();
        });
    }
}

public sealed class CrossPlaneSessionTests
{
    // Dev-http cookie names (the test client is plain http on the Development host).
    private const string AdminCookie = ApiHost::Api.Admins.SessionCookies.SessionCookieNameDevHttp;
    private const string MerchantCookie = ApiHost::Api.Merchants.UserSessionCookies.SessionCookieNameDevHttp;

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path, string cookieName, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{cookieName}={token}");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Each_plane_cookie_authenticates_its_own_plane_and_401s_on_the_other()
    {
        using var factory = new CrossPlaneFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Both cookies are VALID on their own plane first — otherwise the cross-plane 401 would prove nothing.
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/api/v1/admins/me", AdminCookie, Tokens.AdminToken));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/api/v1/merchants/users/me", MerchantCookie, Tokens.MerchantToken));

        // The admin session cookie on a merchant-user route: the merchant scheme never reads it -> 401.
        Assert.Equal(HttpStatusCode.Unauthorized,
            await GetAsync(client, "/api/v1/merchants/users/me", AdminCookie, Tokens.AdminToken));
        // ...and even pasted under the MERCHANT cookie name, the token hash matches no merchant session -> 401.
        Assert.Equal(HttpStatusCode.Unauthorized,
            await GetAsync(client, "/api/v1/merchants/users/me", MerchantCookie, Tokens.AdminToken));

        // The merchant session cookie on an admin route, both ways round -> 401.
        Assert.Equal(HttpStatusCode.Unauthorized,
            await GetAsync(client, "/api/v1/admins/me", MerchantCookie, Tokens.MerchantToken));
        Assert.Equal(HttpStatusCode.Unauthorized,
            await GetAsync(client, "/api/v1/admins/me", AdminCookie, Tokens.MerchantToken));
    }
}
