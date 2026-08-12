using BuildingBlocks.Application;
using Mediator;

namespace Carts.Application;

/// <summary>Opens a new empty cart for the given merchant and returns its id.</summary>
public sealed record CreateCartCommand(Guid MerchantId, string? SaleCode, Guid? OriginatorId = null)
    : ICommand<Guid>, IMerchantScoped;
