extern alias ApiHost;
using ApiHost::Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Producer.Application;

namespace Hosts.Tests;

/// <summary>The producer bound-producer group filter (REQ-17.2/F10): a request that bound a producer scope (a real
/// producer session) passes; a tenant-Bearer caller admitted by the dual-scheme <c>producer</c> policy binds no scope
/// and is denied 403 — so the role/permission catalog reads under <c>/producer</c> cannot leak to a tenant-Bearer token.</summary>
public sealed class ProducerBoundProducerFilterTests
{
    private static readonly ProducerBoundProducerFilter Filter = new();
    private static readonly object Passed = new();

    private static async Task<object?> Run(bool bound)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IProducerScope>(new FakeScope(bound))
                .BuildServiceProvider(),
        };
        var context = EndpointFilterInvocationContext.Create(http);
        return await Filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Passed));
    }

    [Fact]
    public async Task A_bound_producer_passes() => Assert.Same(Passed, await Run(bound: true));

    [Fact]
    public async Task An_unbound_tenant_bearer_caller_is_403() =>
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(await Run(bound: false)).StatusCode);

    private sealed class FakeScope(bool bound) : IProducerScope
    {
        public bool IsBound => bound;
        public ProducerResolution Current => bound
            ? new ProducerResolution(Guid.NewGuid(), "p@org.com", Guid.NewGuid(), new HashSet<string>())
            : throw new InvalidOperationException("not bound");
    }
}
