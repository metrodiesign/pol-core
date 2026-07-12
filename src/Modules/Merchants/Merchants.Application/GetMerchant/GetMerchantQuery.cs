using Mediator;

namespace Merchants.Application.GetMerchant;

/// <summary>Admin read-back of a provisioned merchant by code. NOT IMerchantScoped (admin cross-merchant).</summary>
public sealed record GetMerchantQuery(string Code) : IQuery<MerchantView>;
