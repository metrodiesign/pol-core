using BuildingBlocks.Application;

namespace BuildingBlocks.Tests;

/// <summary>
/// Pins the shape of the sold-check's answer (products-external-source-of-truth REQ-5.14). A bool would be
/// enough to refuse the sale but not to tell 400-at-add-item from 409-at-checkout, and not to let the
/// double-sell audit name the order that holds the document — so the reason and the holder are part of the
/// contract, and narrowing this record back to a flag has to break here rather than in a caller.
/// </summary>
public sealed class DocumentSaleStatusTests
{
    [Fact]
    public void A_held_document_answers_with_the_reason_and_the_holding_order()
    {
        var key = new DocumentKey("69100/900001-10", "VMI");
        var holder = Guid.NewGuid();

        var sold = new DocumentSaleStatus(key, DocumentSaleState.Sold, holder);
        var inFlight = new DocumentSaleStatus(key, DocumentSaleState.PaymentInFlight, holder);

        Assert.NotEqual(sold.State, inFlight.State);
        Assert.Equal(holder, sold.HeldByOrderId);
        Assert.Equal(key, sold.Key);
    }
}
