using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>
/// First-login binding for an invited Scoped admin (REQ-3.5). If an <see cref="User"/> with an unbound
/// subject exists for the caller's verified email, bind the Google subject and resolve it. Returns
/// <see cref="ResolveOutcome.NotFound"/> when there is no matching invite (the host then falls through to
/// the allowlist bootstrap), and <see cref="ResolveOutcome.Suspended"/> when the invite was suspended
/// before first login (the subject is still bound so future logins resolve by subject, but access is denied —
/// REQ-5.6). Checked BEFORE allowlist self-provision so an invited email never collides with the unique Email
/// index.
/// </summary>
public sealed record BindInvitedCommand(string Provider, string Subject, string Email, string CorrelationId)
    : ICommand<ResolveResult>;

public sealed class BindInvitedHandler : ICommandHandler<BindInvitedCommand, ResolveResult>
{
    private readonly IUserRepository _admins;
    private readonly IUnitOfWork _unitOfWork;

    public BindInvitedHandler(
        IUserRepository admins,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork)
    {
        _admins = admins;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<ResolveResult> Handle(BindInvitedCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var account = await _admins.GetByEmailAsync(command.Email, ct);
            if (account is null || account.Subject is not null)
                return ResolveResult.NotFound; // no invite, or already bound (resolved by subject, not here)

            account.BindSubject(command.Provider, command.Subject); // REQ-3.5; no audit — not in REQ-10.1's audited action set
            await _unitOfWork.SaveChangesAsync(ct);

            if (account.Status == UserStatus.Suspended)
                return ResolveResult.Suspended; // REQ-5.6

            var accessible = await ResolveHandler.ResolveAccessibleAsync(account, _admins, ct);
            return ResolveResult.Of(new Resolution(account.Id, account.Email, account.Tier, accessible));
        }, cancellationToken);
    }
}
