extern alias ApiHost;

using Admin.Application;
using Admin.Application.ResolveAdmin;
using Mediator;

namespace Hosts.Tests;

/// <summary>
/// The scoped-admin app-layer floor (REQ-7.1): the <c>IAdminQuery</c> seam must apply the accessible-merchant
/// predicate BEFORE issuing the pol_admin RLS-bypass query. For a Scoped admin asking for a merchant outside its
/// set (or an unknown code) the full projection must NEVER load — fail-closed, no existence leak, and no
/// handler-side error can surface for an inaccessible merchant. These pin that the gate runs before the bypass
/// <c>GetMerchantQuery</c> (a regression here would re-introduce the load-then-reject ordering).
/// </summary>
public sealed class AdminQueryScopeFloorTests
{
    private static readonly Guid InSet = Guid.NewGuid();
    private static readonly Guid OutOfSet = Guid.NewGuid();

    [Fact]
    public async Task A_scoped_admin_requesting_an_out_of_set_merchant_gets_null_without_issuing_the_bypass_query()
    {
        var mediator = new ThrowingMediator();
        var query = new ApiHost::Api.AdminQuery(
            mediator,
            new FakeScope(AccessibleMerchants.Of(new HashSet<Guid> { InSet })),
            new StubDirectory(OutOfSet)); // code resolves to an id NOT in the accessible set

        var result = await query.GetMerchantByCodeAsync("acme", default);

        Assert.Null(result);
        Assert.Equal(0, mediator.SendCount); // floor applied BEFORE the bypass projection (REQ-7.1)
    }

    [Fact]
    public async Task A_scoped_admin_requesting_an_unknown_code_gets_null_without_issuing_the_bypass_query()
    {
        var mediator = new ThrowingMediator();
        var query = new ApiHost::Api.AdminQuery(
            mediator,
            new FakeScope(AccessibleMerchants.Of(new HashSet<Guid> { InSet })),
            new StubDirectory(null)); // unknown code -> no id

        var result = await query.GetMerchantByCodeAsync("ghost", default);

        Assert.Null(result);
        Assert.Equal(0, mediator.SendCount);
    }

    private sealed class FakeScope(AccessibleMerchants accessible) : IAdminScope
    {
        public bool IsBound => true;
        public AdminResolution Current => throw new NotSupportedException();
        public AccessibleMerchants Accessible { get; } = accessible;
    }

    private sealed class StubDirectory(Guid? id) : IAdminMerchantDirectory
    {
        public Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken ct) => Task.FromResult(id);
    }

    /// <summary>Throws if the seam ever issues a query — the fail-closed paths must short-circuit first. Every
    /// Send overload counts + throws so a regression (load-then-reject) surfaces as a non-zero SendCount.</summary>
    private sealed class ThrowingMediator : IMediator
    {
        public int SendCount;

        private ValueTask<T> Hit<T>()
        {
            SendCount++;
            throw new InvalidOperationException("The bypass GetMerchantQuery must not run for an inaccessible merchant.");
        }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => Hit<TResponse>();
        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default) => Hit<TResponse>();
        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken ct = default) => Hit<TResponse>();
        public ValueTask<object?> Send(object message, CancellationToken ct = default) => Hit<object?>();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask Publish<TNotification>(TNotification n, CancellationToken ct = default) where TNotification : INotification => throw new NotSupportedException();
        public ValueTask Publish(object n, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
