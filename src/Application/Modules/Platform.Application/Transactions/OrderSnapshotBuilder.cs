using System.Text.Json;
using System.Text.Json.Serialization;
using Orders.Domain;

namespace Platform.Application.Transactions;

/// <summary>Builds the bounded immutable evidence copied into each Transaction before PSP I/O.</summary>
public static class OrderSnapshotBuilder
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.WriteAsString,
    };

    public static string Build(Order order, DateTime capturedAt, string provenance = "CAPTURED_AT_CONFIRM")
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            provenance = provenance.Trim(),
            capturedAt,
            orderId = order.Id,
            orderVersion = order.Version,
            merchantId = order.MerchantId,
            createdByAccountId = order.CreatedByAccountId,
            ownerSaleId = order.OwnerSaleId,
            ownerBranchId = order.OwnerBranchIdAtCreation,
            businessType = order.BusinessType,
            orderNo = order.OrderNo,
            currency = order.Amount.Currency,
            subtotalAmount = order.SubtotalAmount.Amount,
            orderDiscountAmount = order.OrderDiscountAmount.Amount,
            orderChargeAmount = order.OrderChargeAmount.Amount,
            totalAmount = order.Amount.Amount,
            paymentMethod = order.PaymentChannel,
            items = order.Items.Select(item => new
            {
                productCode = item.ProductCode,
                variantCode = item.VariantCode,
                variantName = item.VariantName,
                quantity = item.Quantity,
                unitPrice = item.UnitPrice.Amount,
                discountAmount = item.Discount.Amount,
                taxAmount = item.TaxAmount.Amount,
                lineAmount = item.LineAmount.Amount,
            }),
        }, Options);
    }
}
