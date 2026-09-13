extern alias ApiHost;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessBffTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-2.14")]
    public async Task Bff_rotation_revokes_the_previous_ticket_and_changes_csrf_token()
    {
        var store = new InMemoryBffStore();
        var manager = CreateManager(store);
        var account = Account.Create(AccountType.Employee, "Employee", Now);

        var first = await manager.CreateAsync(
            account, null, "access-1", "refresh-1", null, "/", default);
        var second = await manager.RotateAsync(
            first.Ticket, account, "access-2", "refresh-2", null, "/", default);

        Assert.NotEqual(first.SessionToken, second.SessionToken);
        Assert.NotEqual(first.CsrfToken, second.CsrfToken);
        Assert.False(first.Ticket.IsLiveAt(Now.AddMinutes(1), account.AuthorizationVersion));
        Assert.True(second.Ticket.IsLiveAt(Now.AddMinutes(1), account.AuthorizationVersion));
        Assert.Equal("access-2", manager.Unprotect(second.Ticket)!.AccessToken);
        Assert.Equal("refresh-2", manager.Unprotect(second.Ticket)!.RefreshToken);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.13")]
    public async Task Bff_csrf_requires_cookie_header_and_ticket_bound_hash()
    {
        var store = new InMemoryBffStore();
        var manager = CreateManager(store);
        var account = Account.Create(AccountType.Employee, "Employee", Now);
        var issue = await manager.CreateAsync(account, null, "access", "refresh", null, "/", default);
        var payload = manager.Unprotect(issue.Ticket)!;
        var filter = new ApiIdentity.BffCsrfFilter();

        var matching = CreateContext(issue.CsrfToken, issue.CsrfToken, issue.Ticket, payload);
        var passed = new object();
        var matchingResult = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(matching),
            _ => ValueTask.FromResult<object?>(passed));
        Assert.Same(passed, matchingResult);

        var mismatch = CreateContext(issue.CsrfToken, "wrong", issue.Ticket, payload);
        var mismatchResult = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(mismatch),
            _ => ValueTask.FromResult<object?>(passed));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(mismatchResult).StatusCode);

        var forgedPayload = payload with
        {
            CsrfHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("different-token")))
        };
        var forged = CreateContext(issue.CsrfToken, issue.CsrfToken, issue.Ticket, forgedPayload);
        var forgedResult = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(forged),
            _ => ValueTask.FromResult<object?>(passed));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(forgedResult).StatusCode);

        var crossOrigin = CreateContext(issue.CsrfToken, issue.CsrfToken, issue.Ticket, payload);
        crossOrigin.Request.Headers.Origin = "https://attacker.example.test";
        var crossOriginResult = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(crossOrigin),
            _ => ValueTask.FromResult<object?>(passed));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(crossOriginResult).StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.13")]
    public async Task Merchant_context_switch_checks_active_access_before_replacing_ticket()
    {
        var store = new InMemoryBffStore();
        var manager = CreateManager(store);
        var account = Account.Create(AccountType.Employee, "Employee", Now);
        var first = await manager.CreateAsync(account, null, "access", "refresh", null, "/", default);
        var selectedMerchant = Guid.NewGuid();
        var query = new FakeIdentityQuery(account, selectedMerchant);
        var http = CreateSessionContext(first.SessionToken);

        var allowed = await ApiIdentity.IdentityAccessEndpoints.SelectMerchantContext(
            http,
            new ApiIdentity.IdentityAccessEndpoints.MerchantContextRequest(selectedMerchant),
            manager,
            store,
            query,
            default);

        Assert.Equal(StatusCodes.Status200OK,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(allowed).StatusCode);
        Assert.False(first.Ticket.IsLiveAt(Now.AddMinutes(1), account.AuthorizationVersion));
        var replacement = store.Tickets.Single(ticket => ticket.Id != first.Ticket.Id);
        Assert.Equal(selectedMerchant, manager.Unprotect(replacement)!.MerchantId);

        var deniedStore = new InMemoryBffStore();
        var deniedManager = CreateManager(deniedStore);
        var deniedFirst = await deniedManager.CreateAsync(account, null, "access", "refresh", null, "/", default);
        var denied = await ApiIdentity.IdentityAccessEndpoints.SelectMerchantContext(
            CreateSessionContext(deniedFirst.SessionToken),
            new ApiIdentity.IdentityAccessEndpoints.MerchantContextRequest(Guid.NewGuid()),
            deniedManager,
            deniedStore,
            new FakeIdentityQuery(account, selectedMerchant),
            default);

        Assert.IsType<ForbidHttpResult>(denied);
        Assert.True(deniedFirst.Ticket.IsLiveAt(Now.AddMinutes(1), account.AuthorizationVersion));
    }

    [Fact]
    [Trait("Requirement", "REQ-2.14")]
    public async Task Refresh_and_logout_handlers_rotate_or_revoke_the_selected_session()
    {
        var store = new InMemoryBffStore();
        var manager = CreateManager(store);
        var account = Account.Create(AccountType.Employee, "Employee", Now);
        var first = await manager.CreateAsync(account, null, "access", "refresh", null, "/", default);

        var refresh = await ApiIdentity.IdentityAccessEndpoints.RefreshSession(
            CreateSessionContext(first.SessionToken), manager, store, new FakeIdentityQuery(account, null), default);
        Assert.Equal(StatusCodes.Status200OK,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(refresh).StatusCode);
        Assert.False(first.Ticket.IsLiveAt(Now.AddMinutes(1), account.AuthorizationVersion));

        var logoutIssue = await manager.CreateAsync(account, null, "access", "refresh", null, "/", default);
        var logoutContext = CreateSessionContext(logoutIssue.SessionToken);
        var logout = await ApiIdentity.IdentityAccessEndpoints.Logout(logoutContext, manager, store, default);

        Assert.Equal(StatusCodes.Status204NoContent,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(logout).StatusCode);
        Assert.False(logoutIssue.Ticket.IsLiveAt(Now.AddMinutes(1), account.AuthorizationVersion));
    }

    private static ApiIdentity.BffSessionManager CreateManager(InMemoryBffStore store) =>
        new(
            store,
            new EphemeralDataProtectionProvider(),
            new FixedClock(Now),
            Options.Create(new ApiIdentity.IdentityAccessOptions { BffSessionMinutes = 60 }));

    private static DefaultHttpContext CreateContext(
        string cookie,
        string header,
        BffSessionTicket ticket,
        ApiIdentity.ProtectedBffTicket payload)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Headers.Cookie = $"{ApiIdentity.BffSessionManager.CsrfCookieName}={cookie}";
        http.Request.Headers[ApiIdentity.BffSessionManager.HeaderName] = header;
        http.Features.Set(new ApiIdentity.BffSessionContext(ticket, payload));
        return http;
    }

    private static DefaultHttpContext CreateSessionContext(string sessionToken)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie =
            $"{ApiIdentity.BffSessionManager.SessionCookieNameDevHttp}={sessionToken}";
        return http;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryBffStore : IBffSessionStore
    {
        private readonly List<BffSessionTicket> _tickets = [];
        public IReadOnlyList<BffSessionTicket> Tickets => _tickets;

        public Task<BffSessionTicket?> FindByHashAsync(byte[] ticketKeyHash, CancellationToken cancellationToken) =>
            Task.FromResult(_tickets.FirstOrDefault(ticket => ticket.TicketKeyHash.SequenceEqual(ticketKeyHash)));

        public void Add(BffSessionTicket ticket) => _tickets.Add(ticket);

        public Task RevokeAsync(BffSessionTicket ticket, DateTime now, CancellationToken cancellationToken)
        {
            ticket.Revoke(now);
            return Task.CompletedTask;
        }

        public Task ReplaceAsync(
            BffSessionTicket current,
            BffSessionTicket replacement,
            DateTime now,
            CancellationToken cancellationToken)
        {
            current.Revoke(now);
            _tickets.Add(replacement);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    }

    private sealed class FakeIdentityQuery(Account account, Guid? allowedMerchant) : IIdentityAccessQuery
    {
        public Task<Account?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult<Account?>(accountId == account.Id ? account : null);

        public Task<SystemClientResolution?> FindSystemClientAsync(string clientId, CancellationToken cancellationToken) =>
            Task.FromResult<SystemClientResolution?>(null);

        public Task<AuthorizationSnapshot?> ResolveAuthorizationAsync(
            Guid accountId, Guid? merchantId, Guid? clientId, CancellationToken cancellationToken)
        {
            if (accountId != account.Id || allowedMerchant is null || merchantId != allowedMerchant)
                return Task.FromResult<AuthorizationSnapshot?>(null);
            return Task.FromResult<AuthorizationSnapshot?>(new AuthorizationSnapshot(
                account.Id,
                account.AccountType,
                account.Status,
                account.AuthorizationVersion,
                merchantId,
                Access.Domain.DataScope.Merchant,
                null,
                null,
                new HashSet<Guid>(),
                new HashSet<Guid>(),
                false,
                new HashSet<string>(),
                clientId));
        }

        public Task<IReadOnlyList<MerchantAccessSummary>> ListMerchantAccessAsync(
            Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MerchantAccessSummary>>([]);
    }
}
