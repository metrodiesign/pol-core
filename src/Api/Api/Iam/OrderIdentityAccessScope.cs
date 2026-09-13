using Accounts.Application;
using Orders.Application;

namespace Api.Iam;

internal sealed class OrderIdentityAccessScope : IOrderIdentityAccessScope
{
    private Guid? accountId;
    private AuthorizationSnapshot? snapshot;

    public bool IsBound => accountId is not null;
    public Guid AccountId => accountId
        ?? throw new InvalidOperationException("Order identity access scope is not bound.");
    public AuthorizationSnapshot Snapshot => snapshot
        ?? throw new InvalidOperationException("Order identity access scope is not bound.");

    public IDisposable Begin(Guid currentAccountId, AuthorizationSnapshot currentSnapshot)
    {
        if (currentAccountId == Guid.Empty)
            throw new ArgumentException("Account id is required.", nameof(currentAccountId));
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        if (IsBound)
            throw new InvalidOperationException("Order identity access scope is already bound.");
        accountId = currentAccountId;
        snapshot = currentSnapshot;
        return new Releaser(this);
    }

    private sealed class Releaser(OrderIdentityAccessScope owner) : IDisposable
    {
        public void Dispose()
        {
            owner.accountId = null;
            owner.snapshot = null;
        }
    }
}
