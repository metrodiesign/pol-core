using Mediator;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Application.MethodPayable;

/// <summary>
/// Answers <see cref="MethodPayableQuery"/> by asking the same two questions
/// <c>CreateSessionHandler</c> asks, in the same order, of the SAME connection the customer pay path will
/// use (2C2P — the only PSP a customer is redirected to). Anything missing reads as "not payable" rather
/// than an error: a merchant with no connection at all cannot be charged on any channel either.
/// </summary>
public sealed class MethodPayableHandler : IQueryHandler<MethodPayableQuery, bool>
{
    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;

    public MethodPayableHandler(IConnectionRepository connections, IPspAdapterFactory adapters)
    {
        _connections = connections;
        _adapters = adapters;
    }

    public async ValueTask<bool> Handle(MethodPayableQuery query, CancellationToken cancellationToken)
    {
        // Malformed input is still a 400 here, exactly as it is at create-session — an unknown method is a
        // caller bug, not a merchant that has the channel switched off.
        var method = PaymentMethods.Normalize(query.Method);

        var connection = await _connections
            .GetAsync(query.MerchantId, Code.TwoCTwoP, cancellationToken).ConfigureAwait(false);

        return connection is { IsEnabled: true }
            && connection.Supports(method)
            && _adapters.For(Code.TwoCTwoP).SupportedMethods.Contains(method);
    }
}
