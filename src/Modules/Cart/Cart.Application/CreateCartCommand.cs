using BuildingBlocks.Application;
using Mediator;

namespace Cart.Application;

/// <summary>Opens a new empty cart for the given tenant and returns its id.</summary>
public sealed record CreateCartCommand(Guid TenantId) : ICommand<Guid>, ITenantScoped;
