using BuildingBlocks.Application;
using Mediator;

namespace Payments.Application.MethodPayable;

/// <summary>
/// Asks whether this merchant can actually be charged through <paramref name="Method"/> today — both
/// halves of the create-session eligibility rule (the merchant's connection enables it AND our adapter can
/// drive it), answered as a boolean instead of thrown. The checkout endpoint asks BEFORE an order exists
/// so a merchant only ever picks a channel that will still be chargeable when the customer pays
/// (purchase-flow-completion REQ-6.1); create-session keeps its own throwing checks, which stay the
/// authority at charge time.
/// </summary>
public sealed record MethodPayableQuery(Guid MerchantId, string Method)
    : IQuery<bool>, IMerchantScoped;
