using BuildingBlocks.Application;
using Identity.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Application.CompleteRegistration;

/// <summary>Consumes a registration ticket + form and creates a pending TenantUser + ExternalLogin +
/// Profile in ONE transaction (REQ-4.2). The subject/email come from the ticket, never the form.</summary>
public sealed record CompleteRegistrationCommand(Guid TicketId, string DisplayName)
    : ICommand<CompleteRegistrationResult>;

public sealed record CompleteRegistrationResult(Guid TenantUserId);

public sealed class CompleteRegistrationHandler
    : ICommandHandler<CompleteRegistrationCommand, CompleteRegistrationResult>
{
    private readonly ITenantUserRepository _users;
    private readonly IRegistrationTicketStore _tickets;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CompleteRegistrationHandler(
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

    public async ValueTask<CompleteRegistrationResult> Handle(
        CompleteRegistrationCommand command, CancellationToken cancellationToken)
    {
        // Everything runs INSIDE the transaction delegate: the admin unit of work clears the change
        // tracker per attempt, so the ticket + new entities are (re)loaded/created on each retry.
        var tenantUserId = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var ticket = await _tickets.GetAsync(command.TicketId, ct)
                ?? throw new NotFoundException("The registration ticket was not found.");

            // Single-use + expiry, fail closed (REQ-3.3/3.4). Not a transient fault -> not retried.
            ticket.Consume(_clock.UtcNow);

            if (await _users.ExistsBySubjectAsync(ticket.Subject, ct))
                throw new ConflictException("This identity is already registered."); // REQ-4.5

            var user = TenantUser.Register(ticket.Subject, ticket.Email, _clock.UtcNow); // subject from ticket, REQ-4.3
            _users.Add(user);
            _users.AddExternalLogin(ExternalLogin.Google(ticket.Subject, user.Id));
            _users.AddProfile(TenantUserProfile.Create(user.Id, command.DisplayName));

            await _unitOfWork.SaveChangesAsync(ct);
            return user.Id;
        }, cancellationToken);

        return new CompleteRegistrationResult(tenantUserId);
    }
}
