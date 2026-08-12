using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Merchants.Application.Users.Roles;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Application.Users;

public static class InvitationTokens
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed record CreateInvitationCommand(
    string Email, Guid MerchantId, Guid ActorUserId, string CorrelationId, int TtlHours,
    InvitationActorAudience ActorAudience = InvitationActorAudience.Merchant,
    IReadOnlyList<string>? IntendedRoleCodes = null,
    string? IdempotencyKey = null)
    : ICommand<CreateInvitationResult>;

public sealed record CreateInvitationResult(Guid InvitationId, string MaskedEmail, DateTime ExpiresAt, string Status);

public sealed class CreateInvitationHandler : ICommandHandler<CreateInvitationCommand, CreateInvitationResult>
{
    private readonly IInvitationRepository _invitations;
    private readonly IManagementAuditWriter _audits;
    private readonly IRegistrationOutboxWriter _outbox;
    private readonly IInvitationDeliveryProtector _protector;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IAdminUserOperationStore? _operations;
    private readonly IRoleRepository? _roles;

    public CreateInvitationHandler(IInvitationRepository invitations, IManagementAuditWriter audits,
        IRegistrationOutboxWriter outbox, IInvitationDeliveryProtector protector,
        IUserUnitOfWork unitOfWork, IClock clock,
        IAdminUserOperationStore? operations = null, IRoleRepository? roles = null)
    {
        _invitations = invitations;
        _audits = audits;
        _outbox = outbox;
        _protector = protector;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operations = operations;
        _roles = roles;
    }

    public async ValueTask<CreateInvitationResult> Handle(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        if (command.TtlHours is < 1 or > 168)
            throw new ArgumentException("Invitation TTL must be between 1 and 168 hours.");
        var now = _clock.UtcNow;
        var expiresAt = now.AddHours(command.TtlHours);
        var email = command.Email.Trim();
        var normalized = MerchantUserInvitation.NormalizeEmail(email);
        var intendedRoles = (command.IntendedRoleCodes ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (command.ActorAudience == InvitationActorAudience.Admin && intendedRoles.Length > 0)
        {
            var resolved = await (_roles ?? throw new InvalidOperationException("Role repository is required."))
                .GetActiveRoleIdsByCodesAsync(command.MerchantId, intendedRoles, cancellationToken);
            var unknown = intendedRoles.Where(x => !resolved.ContainsKey(x)).ToArray();
            if (unknown.Length > 0)
                throw new ConflictException($"Role(s) unknown or inactive: {string.Join(", ", unknown)}.");
        }
        var operation = command.ActorAudience == InvitationActorAudience.Admin ? "merchant-user.invite" : null;
        var intentHash = operation is null ? null : HashIntent(command.MerchantId, normalized, intendedRoles, command.TtlHours);

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (operation is not null)
            {
                if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
                    throw new ArgumentException("Idempotency key is required for an Admin invitation.");
                var store = _operations ?? throw new InvalidOperationException("Admin operation store is required.");
                var replay = await store.FindAsync(
                    command.MerchantId, command.ActorUserId, operation, command.IdempotencyKey, ct);
                if (replay is not null)
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.ASCII.GetBytes(replay.IntentHash),
                            System.Text.Encoding.ASCII.GetBytes(intentHash!)))
                        throw new ConflictException(
                            "Idempotency key was reused with a different intent.", "idempotency_key_reused");
                    return JsonSerializer.Deserialize<CreateInvitationResult>(replay.Result)
                        ?? throw new InvalidOperationException("Stored invitation result is invalid.");
                }
            }

            var rawToken = InvitationTokens.New();
            var protectedToken = _protector.Protect(rawToken);
            var old = await _invitations.FindPendingByNormalizedEmailAsync(normalized, ct);
            if (old is not null)
            {
                old.Revoke(now);
                _audits.Append(MerchantUserManagementAudit.For(command.MerchantId, command.ActorUserId,
                    null, old.Id, MerchantUserManagementAudit.Actions.InviteRevoke, command.CorrelationId, now));
            }

            var invitation = MerchantUserInvitation.Create(command.MerchantId, email, InvitationTokens.Hash(rawToken),
                expiresAt, command.ActorUserId, now, command.ActorAudience, intendedRoles);
            _invitations.Add(invitation);
            _audits.Append(MerchantUserManagementAudit.For(command.MerchantId, command.ActorUserId,
                null, invitation.Id, MerchantUserManagementAudit.Actions.InviteCreate, command.CorrelationId, now));
            _outbox.Enqueue(new MerchantUserInvitationDeliveryRequested(
                invitation.Id, invitation.Email, protectedToken, invitation.ExpiresAt));
            var result = new CreateInvitationResult(
                invitation.Id, PiiMask.Email(invitation.Email)!, invitation.ExpiresAt, "pending");
            if (operation is not null)
                _operations!.Add(AdminUserOperationRecord.Succeeded(
                    command.MerchantId, command.ActorUserId, operation, command.IdempotencyKey!, intentHash!,
                    JsonSerializer.Serialize(result), 201, now));
            await _unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
    }

    private static string HashIntent(Guid merchantId, string email, IReadOnlyList<string> roles, int ttlHours) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { merchantId, email, roles, ttlHours })))).ToLowerInvariant();
}

public sealed record RevokeInvitationCommand(
    Guid InvitationId, Guid MerchantId, Guid ActorUserId, string CorrelationId) : ICommand;

public sealed class RevokeInvitationHandler : ICommandHandler<RevokeInvitationCommand>
{
    private readonly IInvitationRepository _invitations;
    private readonly IManagementAuditWriter _audits;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RevokeInvitationHandler(IInvitationRepository invitations, IManagementAuditWriter audits,
        IUserUnitOfWork unitOfWork, IClock clock)
    {
        _invitations = invitations; _audits = audits; _unitOfWork = unitOfWork; _clock = clock;
    }

    public async ValueTask<Unit> Handle(RevokeInvitationCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var invitation = await _invitations.FindByIdAsync(command.InvitationId, ct)
                ?? throw new NotFoundException("Invitation not found.");
            invitation.Revoke(_clock.UtcNow);
            _audits.Append(MerchantUserManagementAudit.For(command.MerchantId, command.ActorUserId,
                null, invitation.Id, MerchantUserManagementAudit.Actions.InviteRevoke,
                command.CorrelationId, _clock.UtcNow));
            await _unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
        return Unit.Value;
    }
}

public sealed record InvitationResolution(Guid InvitationId, Guid MerchantId, string Email, string NormalizedEmail);
public sealed record ResolveInvitationTokenQuery(string RawToken) : IQuery<InvitationResolution?>;
public sealed record ResolveInvitationByIdQuery(Guid InvitationId) : IQuery<InvitationResolution?>;

public sealed class ResolveInvitationTokenHandler(IInvitationRepository invitations, IClock clock)
    : IQueryHandler<ResolveInvitationTokenQuery, InvitationResolution?>
{
    public async ValueTask<InvitationResolution?> Handle(ResolveInvitationTokenQuery query, CancellationToken cancellationToken)
    {
        if (query.RawToken is not { Length: 43 } rawToken
            || rawToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return null;
        var invitation = await invitations.FindByTokenHashUnfilteredAsync(InvitationTokens.Hash(rawToken), cancellationToken);
        return invitation is not null && invitation.IsPending(clock.UtcNow)
            ? new(invitation.Id, invitation.MerchantId, invitation.Email, invitation.NormalizedEmail)
            : null;
    }
}

public sealed class ResolveInvitationByIdHandler(IInvitationRepository invitations, IClock clock)
    : IQueryHandler<ResolveInvitationByIdQuery, InvitationResolution?>
{
    public async ValueTask<InvitationResolution?> Handle(ResolveInvitationByIdQuery query, CancellationToken cancellationToken)
    {
        var invitation = await invitations.FindByIdUnfilteredAsync(query.InvitationId, cancellationToken);
        return invitation is not null && invitation.IsPending(clock.UtcNow)
            ? new(invitation.Id, invitation.MerchantId, invitation.Email, invitation.NormalizedEmail)
            : null;
    }
}

public sealed record MerchantUserListItem(
    Guid UserId, string DisplayName, string MaskedEmail, string? MaskedPhone, string? ProducerCode,
    string? MaskedLicenseNumber, UserStatus Status, IReadOnlyList<string> RoleCodes, DateTime CreatedAt,
    Guid? MerchantId, long Version);

public sealed record MerchantUserDetail(
    Guid UserId, string DisplayName, string MaskedEmail, string? MaskedPhone, string? ProducerCode,
    string? MaskedLicenseNumber, UserStatus Status, IReadOnlyList<string> RoleCodes, DateTime CreatedAt,
    IdentityType PersonType, string? MaskedIdentityNumber, IReadOnlySet<string> EffectivePermissions,
    Guid? MerchantId, long Version);

public sealed record MerchantUserEditView(
    Guid UserId, string FirstName, string LastName, IdentityType PersonType, string MaskedEmail,
    string? MaskedIdentityNumber, string? ProducerCode, string? LicenseNumber, string? Phone, long Version);

public sealed record ListMerchantUsersQuery(
    Guid MerchantId,
    bool AdminRead = false,
    bool AdminUnrestricted = false,
    IReadOnlySet<Guid>? AccessibleMerchantIds = null)
    : PagedQuery, IQuery<PagedResult<MerchantUserListItem>>;

public sealed class ListMerchantUsersHandler(IUserRepository users, IRoleRepository roles)
    : IQueryHandler<ListMerchantUsersQuery, PagedResult<MerchantUserListItem>>
{
    public async ValueTask<PagedResult<MerchantUserListItem>> Handle(
        ListMerchantUsersQuery query, CancellationToken cancellationToken)
    {
        Guid? roleId = null;
        var roleFilter = query.Filters.FirstOrDefault(f => f.Field == "roleCode" && f.Operator == FilterOperator.Equals);
        if (roleFilter is not null)
        {
            if (query.AdminRead && query.MerchantId == Guid.Empty)
                throw new ArgumentException("roleCode filter requires an explicit merchantId for Admin aggregate reads.");
            var code = StringValue(roleFilter.Value);
            var resolved = await roles.GetRoleIdsByCodesAsync(query.MerchantId, [code], cancellationToken);
            roleId = resolved.TryGetValue(code, out var id) ? id : Guid.Empty;
        }

        var page = query.AdminRead
            ? await users.ListForAdminAsync(query, roleId,
                query.MerchantId == Guid.Empty ? null : query.MerchantId,
                query.AdminUnrestricted, query.AccessibleMerchantIds ?? new HashSet<Guid>(), cancellationToken)
            : await users.ListAsync(query, roleId, cancellationToken);
        var items = new List<MerchantUserListItem>(page.Items.Count);
        foreach (var user in page.Items)
        {
            var merchantId = user.MerchantId;
            items.Add(new MerchantUserListItem(user.Id, user.DisplayName, PiiMask.Email(user.Email)!,
                PiiMask.Last4(user.Phone), user.SaleCode, PiiMask.Last4(user.LicenseNumber), user.Status,
                merchantId is { } bound
                    ? await roles.ListActiveRoleCodesForUserAsync(user.Id, bound, cancellationToken)
                    : [],
                user.CreatedAt, merchantId, user.Version));
        }
        return new PagedResult<MerchantUserListItem>(items, page.Page, page.Limit, page.Total);
    }

    private static string StringValue(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } element && element.GetString() is { Length: > 0 } text
            ? text : throw new ArgumentException("roleCode filter value must be a non-empty string.");
}

public sealed record GetMerchantUserQuery(
    Guid UserId, Guid MerchantId,
    bool AdminRead = false,
    bool AdminUnrestricted = false,
    IReadOnlySet<Guid>? AccessibleMerchantIds = null) : IQuery<MerchantUserDetail?>;
public sealed class GetMerchantUserHandler(IUserRepository users, IRoleRepository roles)
    : IQueryHandler<GetMerchantUserQuery, MerchantUserDetail?>
{
    public async ValueTask<MerchantUserDetail?> Handle(GetMerchantUserQuery query, CancellationToken cancellationToken)
    {
        var user = query.AdminRead
            ? await users.FindByIdForAdminAsync(query.UserId, query.AdminUnrestricted,
                query.AccessibleMerchantIds ?? new HashSet<Guid>(), cancellationToken)
            : await users.FindByIdAsync(query.UserId, cancellationToken);
        if (user is null) return null;
        var merchantId = user.MerchantId ?? (query.MerchantId == Guid.Empty ? null : query.MerchantId);
        return new MerchantUserDetail(user.Id, user.DisplayName, PiiMask.Email(user.Email)!, PiiMask.Last4(user.Phone),
            user.SaleCode, PiiMask.Last4(user.LicenseNumber), user.Status,
            merchantId is { } bound
                ? await roles.ListActiveRoleCodesForUserAsync(user.Id, bound, cancellationToken)
                : [], user.CreatedAt,
            user.IdentityType, PiiMask.Last4(user.IdentityNumber),
            merchantId is { } permissionMerchant
                ? await roles.ListEffectivePermissionsAsync(user.Id, permissionMerchant, cancellationToken)
                : new HashSet<string>(),
            merchantId, user.Version);
    }
}

public sealed record GetMerchantUserEditQuery(
    Guid UserId, Guid MerchantId, Guid ActorUserId, string CorrelationId) : IQuery<MerchantUserEditView?>;
public sealed class GetMerchantUserEditHandler(
    IUserRepository users, IManagementAuditWriter audits, IUserUnitOfWork unitOfWork, IClock clock)
    : IQueryHandler<GetMerchantUserEditQuery, MerchantUserEditView?>
{
    public async ValueTask<MerchantUserEditView?> Handle(GetMerchantUserEditQuery query, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(query.UserId, cancellationToken);
        if (user is null) return null;
        audits.Append(MerchantUserManagementAudit.For(query.MerchantId, query.ActorUserId, user.Id, null,
            MerchantUserManagementAudit.Actions.Reveal, query.CorrelationId, clock.UtcNow));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(user.Id, user.FirstName, user.LastName, user.IdentityType, PiiMask.Email(user.Email)!,
            PiiMask.Last4(user.IdentityNumber), user.SaleCode, user.LicenseNumber, user.Phone, user.Version);
    }
}

public sealed record UpdateMerchantUserCommand(
    Guid UserId, Guid MerchantId, Guid ActorUserId, string FirstName, string LastName,
    string? ProducerCode, string? LicenseNumber, string? Phone, string CorrelationId,
    long? ExpectedVersion = null) : ICommand<UpdateMerchantUserResult>;

public sealed record UpdateMerchantUserResult(Guid UserId, long Version);

public sealed class UpdateMerchantUserHandler(
    IUserRepository users, IManagementAuditWriter audits, IUserUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<UpdateMerchantUserCommand, UpdateMerchantUserResult>
{
    public async ValueTask<UpdateMerchantUserResult> Handle(
        UpdateMerchantUserCommand command, CancellationToken cancellationToken)
    {
        var version = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var user = await users.FindByIdAsync(command.UserId, ct) ?? throw new NotFoundException("Merchant user not found.");
            if (command.ExpectedVersion is { } expectedVersion)
                user.EnsureVersion(expectedVersion);
            user.UpdateProfile(command.FirstName, command.LastName, command.ProducerCode, command.LicenseNumber, command.Phone);
            audits.Append(MerchantUserManagementAudit.For(command.MerchantId, command.ActorUserId, user.Id, null,
                MerchantUserManagementAudit.Actions.Update, command.CorrelationId, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            return user.Version;
        }, cancellationToken);
        return new UpdateMerchantUserResult(command.UserId, version);
    }
}

public enum MerchantUserLifecycleAction { Approve, Reject, Suspend, Reactivate }
public sealed record ChangeMerchantUserLifecycleCommand(
    Guid UserId, Guid MerchantId, Guid ActorUserId, MerchantUserLifecycleAction Action, string CorrelationId) : ICommand;

public sealed class ChangeMerchantUserLifecycleHandler : ICommandHandler<ChangeMerchantUserLifecycleCommand>
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IActiveManagerGuard _managerGuard;
    private readonly ISessionStore _sessions;
    private readonly IManagementAuditWriter _audits;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ChangeMerchantUserLifecycleHandler(IUserRepository users, IRoleRepository roles,
        IActiveManagerGuard managerGuard, ISessionStore sessions, IManagementAuditWriter audits,
        IUserUnitOfWork unitOfWork, IClock clock)
    {
        _users = users; _roles = roles; _managerGuard = managerGuard; _sessions = sessions;
        _audits = audits; _unitOfWork = unitOfWork; _clock = clock;
    }

    public async ValueTask<Unit> Handle(ChangeMerchantUserLifecycleCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var user = await _users.FindByIdAsync(command.UserId, ct) ?? throw new NotFoundException("Merchant user not found.");
            if (command.UserId == command.ActorUserId
                && command.Action is MerchantUserLifecycleAction.Reject or MerchantUserLifecycleAction.Suspend)
                throw new ConflictException("You cannot reject or suspend yourself.");

            var now = _clock.UtcNow;
            string auditAction;
            switch (command.Action)
            {
                case MerchantUserLifecycleAction.Approve:
                    {
                        if (user.Status != UserStatus.PendingApproval)
                            throw new InvalidOperationException(
                                $"Cannot approve an account in status {user.Status}; it must be PendingApproval.");
                        user.Approve(command.MerchantId, now);
                        var staff = await _roles.GetActiveRoleIdsByCodesAsync(command.MerchantId, ["merchant_staff"], ct);
                        if (!staff.TryGetValue("merchant_staff", out var roleId))
                            throw new ConflictException("The shared merchant_staff role is unavailable.");
                        if (!await _roles.AssignmentExistsAsync(user.Id, roleId, ct))
                            _roles.AddAssignment(RoleAssignment.Create(user.Id, roleId, command.MerchantId, command.ActorUserId, now));
                        auditAction = MerchantUserManagementAudit.Actions.Approve;
                        break;
                    }
                case MerchantUserLifecycleAction.Reject:
                    user.Reject(now);
                    await _sessions.RevokeAllForUserAsync(user.Id, ct);
                    auditAction = MerchantUserManagementAudit.Actions.Reject;
                    break;
                case MerchantUserLifecycleAction.Suspend:
                    await EnsureNotLastManagerAsync(user, command.MerchantId, ct);
                    user.Suspend(now);
                    await _sessions.RevokeAllForUserAsync(user.Id, ct);
                    auditAction = MerchantUserManagementAudit.Actions.Suspend;
                    break;
                case MerchantUserLifecycleAction.Reactivate:
                    user.Reactivate(now);
                    auditAction = MerchantUserManagementAudit.Actions.Reactivate;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(command.Action));
            }

            _audits.Append(MerchantUserManagementAudit.For(command.MerchantId, command.ActorUserId, user.Id, null,
                auditAction, command.CorrelationId, now));
            await _unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
        return Unit.Value;
    }

    private async Task EnsureNotLastManagerAsync(User user, Guid merchantId, CancellationToken ct)
    {
        var manager = await _roles.GetActiveRoleIdsByCodesAsync(merchantId, ["merchant_manager"], ct);
        if (!manager.TryGetValue("merchant_manager", out var managerRoleId)) return;
        if (await _roles.AssignmentExistsAsync(user.Id, managerRoleId, ct)
            && await _managerGuard.CountActiveUsersWithRoleAsync(merchantId, managerRoleId, ct) <= 1)
            throw new ConflictException("The last active merchant manager cannot be suspended.");
    }
}

public sealed class MerchantUserInvitationDeliveryHandler(
    IInvitationDeliveryProtector protector, IInvitationEmailSender sender)
    : INotificationHandler<MerchantUserInvitationDeliveryRequested>
{
    public async ValueTask Handle(MerchantUserInvitationDeliveryRequested notification, CancellationToken cancellationToken)
    {
        if (!protector.TryUnprotect(notification.ProtectedToken, out var rawToken))
            throw new InvalidOperationException("Invitation delivery token could not be unprotected.");
        await sender.SendAsync(notification.Email, rawToken, cancellationToken);
    }
}
