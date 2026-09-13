using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.GetSession;

public sealed record ListSessionsQuery : PagedQuery, IQuery<PagedResult<PaymentSessionListItem>>, IMerchantScoped;

public sealed record PaymentSessionListItem(
    Guid PaymentSessionId,
    Guid OrderId,
    Money Amount,
    string Method,
    Code Psp,
    SessionStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class ListSessionsHandler : IQueryHandler<ListSessionsQuery, PagedResult<PaymentSessionListItem>>
{
    private readonly ISessionRepository _sessions;

    public ListSessionsHandler(ISessionRepository sessions) => _sessions = sessions;

    public async ValueTask<PagedResult<PaymentSessionListItem>> Handle(
        ListSessionsQuery query,
        CancellationToken cancellationToken)
    {
        var page = await _sessions.ListAsync(query, cancellationToken).ConfigureAwait(false);
        return new PagedResult<PaymentSessionListItem>(
            [.. page.Items.Select(session => new PaymentSessionListItem(
                session.Id,
                session.OrderId,
                session.Amount,
                session.Method,
                session.Psp,
                session.Status,
                session.CreatedAt,
                session.UpdatedAt))],
            page.Page,
            page.Limit,
            page.Total);
    }
}
