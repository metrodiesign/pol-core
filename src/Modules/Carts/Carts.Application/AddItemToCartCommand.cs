using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Carts.Application;

/// <summary>Adds one insurance-document line to an open cart. Every value here comes from the row the
/// upstream returned for this request — the document number, its sale code, its group, and the price minted
/// from its <c>TotalPremium</c> — never from the request body (products-external-source-of-truth
/// REQ-4.1/4.2/4.7).</summary>
public sealed record AddItemToCartCommand(
    Guid CartId,
    Guid MerchantId,
    string DocumentNo,
    string SaleCode,
    string ProductGroup,
    int Quantity,
    Money UnitPrice) : ICommand<AddItemResult>, IMerchantScoped;
