extern alias ApiHost;

using System.Security.Claims;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Persistence.MerchantUsers.Outbox;
using HttpActorContext = ApiHost::Api.HttpActorContext;
using MerchantRequestWriteAuthorizer = ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer;
using MerchantUserAccount = Merchants.Domain.Users.User;
using MerchantRoleAssignment = Merchants.Domain.Users.Roles.RoleAssignment;

namespace Hosts.Tests;

/// <summary>
/// bugfix-merchant-prebind-wiring T1 (defect D3, F5): <see cref="HttpActorContext"/> is a Scoped service that
/// gets constructed DURING session authentication (auth handler ctor → ISessionStore → MerchantUserDbContext →
/// IActorContext) — BEFORE the handler sets the authenticated principal. The claims must therefore be read
/// lazily per access, not snapshotted in the constructor, or every session-authenticated request keeps
/// CurrentMerchant == Guid.Empty for its whole lifetime in production.
/// </summary>
public sealed class HttpActorContextTests
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ClaimsPrincipal PrincipalFor(Guid merchantId, Guid? userId = null)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim("merchant_id", merchantId.ToString()));
        if (userId is { } uid)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, uid.ToString()));
        return new ClaimsPrincipal(identity);
    }

    private static (HttpActorContext Actor, DefaultHttpContext Http, AmbientActor Ambient) Build(
        IReadOnlyDictionary<string, string?>? config = null, ClaimsPrincipal? initialUser = null)
    {
        var httpContext = new DefaultHttpContext();
        if (initialUser is not null)
            httpContext.User = initialUser;
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();
        var ambient = new AmbientActor();
        return (new HttpActorContext(accessor, configuration, ambient), httpContext, ambient);
    }

    // ---------------------------------------------------------------- F5: claims must be observed lazily

    [Fact]
    public void A_merchant_id_claim_set_after_construction_is_still_honored()
    {
        // Production ordering: the scoped HttpActorContext is constructed while the request is still
        // anonymous; the session auth handler sets the principal AFTERWARDS (UserSessionAuthenticationHandler).
        var (actor, httpContext, _) = Build();

        httpContext.User = PrincipalFor(MerchantA, UserA);

        Assert.True(actor.HasActor);
        Assert.Equal(MerchantA, actor.MerchantId);
        Assert.Equal(UserA, actor.UserId);
    }

    // ---------------------------------------------------------------- B10: ambient binding precedence

    [Fact]
    public void An_ambient_binding_takes_precedence_over_the_claim()
    {
        var (actor, _, ambient) = Build(initialUser: PrincipalFor(MerchantA));

        using var _scope = ambient.Begin(MerchantB);

        Assert.Equal(MerchantB, actor.MerchantId);
    }

    // ---------------------------------------------------------------- B11: dev fallback semantics unchanged

    [Fact]
    public void The_dev_fallback_merchant_applies_only_when_no_claim_exists()
    {
        var (actor, _, _) = Build(new Dictionary<string, string?> { ["Merchant:DevMerchantId"] = MerchantB.ToString() });

        Assert.True(actor.HasActor);
        Assert.Equal(MerchantB, actor.MerchantId);
    }

    [Fact]
    public void A_real_claim_beats_the_dev_fallback()
    {
        var (actor, httpContext, _) = Build(new Dictionary<string, string?> { ["Merchant:DevMerchantId"] = MerchantB.ToString() });

        httpContext.User = PrincipalFor(MerchantA);

        Assert.Equal(MerchantA, actor.MerchantId);
    }

    // ------------------------------------------------- products-external-source-of-truth REQ-4.8/4.9: sale code

    // The catalogue search runs under the authenticated account's own sale code, so the actor is where that
    // value comes from — and, being a claim, it obeys the same F5 lazy rule as merchant_id: the session handler
    // sets the principal after this scoped service already exists.
    [Fact]
    public void The_sale_code_claim_set_after_construction_is_still_honored()
    {
        var (actor, httpContext, _) = Build();

        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim("sale_code", "77001"));
        httpContext.User = new ClaimsPrincipal(identity);

        Assert.Equal("77001", actor.SaleCode);
    }

    // An account with no sale code bound has none here either — null, never a fallback. The catalogue path turns
    // that into a 403 (REQ-4.9); inventing a value would search a different party's documents.
    [Fact]
    public void An_actor_without_the_claim_has_no_sale_code()
    {
        var (actor, _, _) = Build(initialUser: PrincipalFor(MerchantA, UserA));

        Assert.Null(actor.SaleCode);
    }
}

/// <summary>
/// bugfix-merchant-prebind-wiring T1 (defect D2, B4): pins the REAL <see cref="MerchantRequestWriteAuthorizer"/>
/// boundary — an unbound actor may write NULL/Empty-tenant rows and the registration-outbox sentinel, but a
/// real merchant target (exactly what an admin approve produces: User.MerchantId NULL→value + a tenant-keyed
/// RoleAssignment insert) is denied without a matching bound merchant actor. That denial is CORRECT for the
/// self-service capability and stays; the admin plane needs its own capability (T3), never a loosening of this one.
/// </summary>
public sealed class MerchantRequestWriteAuthorizerTests
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class StubActor(Guid? merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId ?? throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => merchantId.HasValue;
    }

    [Fact]
    public void An_unbound_actor_may_write_empty_tenant_and_sentinel_rows_only()
    {
        var floor = new MerchantRequestWriteAuthorizer(new StubActor(null));

        Assert.True(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Insert, Guid.Empty));
        Assert.True(floor.CanWrite(typeof(MerchantUserOutbox), WriteOperation.Insert, MerchantRegistrationOutboxSentinel.MerchantId));

        // The exact write set an admin approve produces — denied for an unbound caller (defect D2's mechanism).
        Assert.False(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantA));
        Assert.False(floor.CanWrite(typeof(MerchantRoleAssignment), WriteOperation.Insert, MerchantA));
    }

    [Fact]
    public void A_bound_actor_may_write_only_its_own_merchant()
    {
        var floor = new MerchantRequestWriteAuthorizer(new StubActor(MerchantA));

        Assert.True(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantA));
        Assert.False(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, Guid.NewGuid()));
    }
}
