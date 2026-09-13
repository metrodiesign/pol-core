using Accounts.Application;

namespace Orders.Application;

/// <summary>Scoped identity authorization snapshot carried from the HTTP identity gate into Commerce. Its
/// presence is separate from the legacy merchant-user GUID so Account IDs can never be mistaken for legacy
/// MerchantUser IDs by the Commerce query filter.</summary>
public interface IOrderIdentityAccessScope
{
    bool IsBound { get; }
    Guid AccountId { get; }
    AuthorizationSnapshot Snapshot { get; }
    IDisposable Begin(Guid accountId, AuthorizationSnapshot snapshot);
}
