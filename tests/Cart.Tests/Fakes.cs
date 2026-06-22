using BuildingBlocks.Application;
using Cart.Application;
using CartAggregate = Cart.Domain.Cart;

namespace Cart.Tests;

internal sealed class FakeCartRepository : ICartRepository
{
    private readonly List<CartAggregate> _carts = [];

    public FakeCartRepository(params CartAggregate[] seed) => _carts.AddRange(seed);

    public void Add(CartAggregate cart) => _carts.Add(cart);

    public Task<CartAggregate?> GetAsync(Guid cartId, CancellationToken cancellationToken) =>
        Task.FromResult(_carts.FirstOrDefault(c => c.Id == cartId));
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.FromResult(0);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await operation(cancellationToken);
}
