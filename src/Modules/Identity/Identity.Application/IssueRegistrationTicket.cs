using BuildingBlocks.Application;
using Identity.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Application.IssueRegistrationTicket;

/// <summary>Issues a registration ticket to an authenticated Google identity that has no ExternalLogin yet
/// (REQ-4.1). Subject/email/hd come from the validated token (set by the host), never the body.</summary>
public sealed record IssueRegistrationTicketCommand(string Subject, string Email, string? HostedDomain)
    : ICommand<IssueRegistrationTicketResult>;

public sealed record IssueRegistrationTicketResult(Guid TicketId, DateTime ExpiresAtUtc);

public sealed class IssueRegistrationTicketHandler
    : ICommandHandler<IssueRegistrationTicketCommand, IssueRegistrationTicketResult>
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ITenantUserRepository _users;
    private readonly IRegistrationTicketStore _tickets;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public IssueRegistrationTicketHandler(
        ITenantUserRepository users,
        IRegistrationTicketStore tickets,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _users = users;
        _tickets = tickets;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<IssueRegistrationTicketResult> Handle(
        IssueRegistrationTicketCommand command, CancellationToken cancellationToken)
    {
        if (await _users.ExistsBySubjectAsync(command.Subject, cancellationToken))
            throw new ConflictException("This identity is already registered."); // REQ-4.5 / 10.4

        var ticket = RegistrationTicket.Issue(command.Subject, command.Email, command.HostedDomain, _clock.UtcNow, Ttl);
        _tickets.Add(ticket);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new IssueRegistrationTicketResult(ticket.Id, ticket.ExpiresAtUtc);
    }
}
