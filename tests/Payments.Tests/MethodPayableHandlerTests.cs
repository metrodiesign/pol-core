using Payments.Application.MethodPayable;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Tests;

/// <summary>
/// purchase-flow-completion REQ-6.1 — the checkout-time answer to "can this merchant actually be charged on
/// this channel?". It must agree with the two throwing checks <c>CreateSessionHandler</c> runs at charge
/// time (connection enabled + enables the method, adapter honours the method): a channel that passes here
/// and then 409s at pay time is exactly the dead payment link this gate exists to prevent.
/// </summary>
public sealed class MethodPayableHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly DateTime Now = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

    private static MethodPayableHandler Build(
        Connection? connection = null,
        params string[] adapterMethods)
    {
        var connections = connection is null
            ? new FakeConnectionRepository()
            : new FakeConnectionRepository(connection);

        return new MethodPayableHandler(
            connections,
            new FakePspAdapterFactory(new FakePspAdapter(
                Code.TwoCTwoP,
                adapterMethods.Length == 0
                    ? [PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment]
                    : adapterMethods)),
            new DefaultPspSelection(Code.TwoCTwoP));
    }

    /// <summary><see cref="Connection"/> has no Disable() — IsEnabled only ever goes false by EF
    /// materialising such a row — so a disabled connection is reproduced the way EF does it (private
    /// setter), the same trick <c>ConnectionEligibilityTests</c> uses.</summary>
    private static Connection Connected(string enabledMethods, bool enabled = true)
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, enabledMethods, "psp/demo/2c2p", Now);
        if (!enabled)
            typeof(Connection).GetProperty(nameof(Connection.IsEnabled))!.SetValue(connection, false);

        return connection;
    }

    [Theory]
    [InlineData(PaymentMethods.Card)]
    [InlineData(PaymentMethods.PromptPay)]
    [InlineData(PaymentMethods.Installment)]
    public async Task A_method_both_the_connection_and_the_adapter_admit_is_payable(string method)
    {
        var handler = Build(Connected("card,promptpay,installment"));

        Assert.True(await handler.Handle(new MethodPayableQuery(MerchantId, method), CancellationToken.None));
    }

    [Fact]
    public async Task A_method_the_connection_does_not_enable_is_not_payable()
    {
        // The commercial arrangement is the first half of eligibility — the seeded merchants deliberately
        // differ here, and a merchant limited to cards must not be offered PromptPay at checkout.
        var handler = Build(Connected("card"));

        Assert.False(await handler.Handle(
            new MethodPayableQuery(MerchantId, PaymentMethods.PromptPay), CancellationToken.None));
    }

    [Fact]
    public async Task A_method_the_adapter_cannot_drive_is_not_payable()
    {
        // The second half: the connection may enable a channel our adapter has no channel code for.
        var handler = Build(Connected("card,promptpay,installment"), PaymentMethods.Card);

        Assert.False(await handler.Handle(
            new MethodPayableQuery(MerchantId, PaymentMethods.Installment), CancellationToken.None));
    }

    [Fact]
    public async Task A_disabled_connection_makes_every_method_unpayable()
    {
        var handler = Build(Connected("card,promptpay,installment", enabled: false));

        Assert.False(await handler.Handle(
            new MethodPayableQuery(MerchantId, PaymentMethods.Card), CancellationToken.None));
    }

    [Fact]
    public async Task A_merchant_with_no_2c2p_connection_can_be_charged_on_nothing()
    {
        // Absent reads as "not payable", not as an error: there is no connection to charge through, which is
        // the same outcome for the customer as a channel that is switched off.
        var handler = Build();

        Assert.False(await handler.Handle(
            new MethodPayableQuery(MerchantId, PaymentMethods.Card), CancellationToken.None));
    }

    [Fact]
    public async Task Another_merchants_connection_is_not_this_merchants_eligibility()
    {
        var handler = Build(Connection.Create(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Code.TwoCTwoP, "card,promptpay,installment", "psp/other/2c2p", Now));

        Assert.False(await handler.Handle(
            new MethodPayableQuery(MerchantId, PaymentMethods.Card), CancellationToken.None));
    }

    [Fact]
    public async Task A_method_outside_the_vocabulary_is_a_400_not_a_false()
    {
        // Malformed input stays malformed input (ArgumentException -> 400), the same class of failure
        // create-session gives it — "false" here would read to the caller as a merchant setting.
        var handler = Build(Connected("card,promptpay,installment"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(new MethodPayableQuery(MerchantId, "paypal"), CancellationToken.None));
    }

    [Fact]
    public async Task Eligibility_uses_configured_default_psp_without_fallback()
    {
        var connection = Connection.Create(
            MerchantId, Code.Omise, "card", "psp/demo/omise", Now);
        var handler = new MethodPayableHandler(
            new FakeConnectionRepository(connection),
            new FakePspAdapterFactory(new FakePspAdapter(Code.Omise, PaymentMethods.Card)),
            new DefaultPspSelection(Code.Omise));

        Assert.True(await handler.Handle(
            new MethodPayableQuery(MerchantId, PaymentMethods.Card), CancellationToken.None));
    }
}
