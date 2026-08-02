using BuildingBlocks.Application;
using Payments.Application.GetSession;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// purchase-flow-completion REQ-8.8. The whole point of this file is the EXCEPTION TYPE: the handler used to
/// raise InvalidOperationException, which the shared ProblemDetails handler maps to 409 — so a merchant
/// asking for a session that is not theirs (or not there) was told "conflict" for a request that was never
/// in conflict with anything. Absence is 404.
/// </summary>
public sealed class GetSessionHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Created = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_session_that_is_not_there_is_not_found()
    {
        var handler = new GetSessionHandler(new FakeSessionRepository());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new GetSessionQuery(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task A_session_reads_back_with_its_status_and_charge()
    {
        var session = Session.Create(
            MerchantId, OrderId, Money.Of(15000m, "THB"), PaymentMethods.Card, Code.TwoCTwoP, Created);
        session.BeginRedirect(Created);
        session.SetPspCharge("INV-1", "https://2c2p.test/hosted/pay", Created);
        var handler = new GetSessionHandler(new FakeSessionRepository(session));

        var view = await handler.Handle(new GetSessionQuery(session.Id), default);

        Assert.Equal(session.Id, view.PaymentSessionId);
        Assert.Equal(SessionStatus.Redirected, view.Status);
        Assert.Equal("INV-1", view.PspExternalChargeId);
    }
}
