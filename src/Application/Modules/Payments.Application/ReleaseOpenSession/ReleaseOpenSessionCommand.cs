using BuildingBlocks.Application;
using Mediator;

namespace Payments.Application.ReleaseOpenSession;

/// <summary>
/// Frees an order from the payment session that is holding it, so the order can be cancelled (REQ-4.1-4.3).
/// Succeeds only when the session is provably not going to collect money; anything else — including "we could
/// not reach the PSP to find out" — is a <see cref="ConflictException"/>, never a silent release.
/// Merchant-scoped. Returns nothing: the whole answer is whether it threw.
/// </summary>
public sealed record ReleaseOpenSessionCommand(Guid OrderId) : ICommand<Unit>, IMerchantScoped;
