extern alias ApiHost;
using ApiHost::Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;

namespace Hosts.Tests;

/// <summary>The merchant-bound group filter (REQ-17.2/F10): a request that bound a merchant-user scope (a real
/// merchant-user session) passes; any other caller binds no scope and is denied 403 — so the role/permission
/// catalog reads under <c>/merchant-users</c> cannot leak to an unbound caller.</summary>
public sealed class MerchantBoundFilterTests
{
    private static readonly MerchantBoundFilter Filter = new();
    private static readonly object Passed = new();

    private static async Task<object?> Run(bool bound)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IUserScope>(new FakeScope(bound))
                .BuildServiceProvider(),
        };
        var context = EndpointFilterInvocationContext.Create(http);
        return await Filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Passed));
    }

    [Fact]
    public async Task A_bound_merchant_user_passes() => Assert.Same(Passed, await Run(bound: true));

    [Fact]
    public async Task An_unbound_scope_is_403() =>
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(await Run(bound: false)).StatusCode);

    private sealed class FakeScope(bool bound) : IUserScope
    {
        public bool IsBound => bound;
        public Resolution Current => bound
            ? new Resolution(Guid.NewGuid(), "p@org.com", Guid.NewGuid(), new HashSet<string>())
            : throw new InvalidOperationException("not bound");
    }
}
