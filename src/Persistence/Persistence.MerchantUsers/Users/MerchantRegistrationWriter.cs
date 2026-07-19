using BuildingBlocks.Application;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

/// <summary>
/// rls-to-query-filter task 5 (design.md "Pre-owner-bind READS vs WRITES" — <c>IApproveRegistrationWriter</c> /
/// <c>IRejectRegistrationWriter</c>, R1-v7 #1): the one-time tenant-key transition NULL -&gt; verified target
/// merchant for a PENDING <c>merch.Users</c> row. Self-contained inside <c>Persistence.MerchantUsers</c> (not
/// exposed through <c>Merchants.Application</c> — that assembly cannot be referenced from here, the same
/// boundary task 5's login-by-subject ports hit first). Conditional set-based DML, not a tracked
/// load-then-SaveChanges: the DML's own <c>WHERE Subject=@s AND MerchantId IS NULL AND Status=Pending</c>
/// predicate IS the immutability enforcement for this one sanctioned NULL-&gt;value transition
/// (<c>GuardedRuntimeDbContext</c>'s tenant-key guard only fires for the change-tracked SaveChanges path, which
/// this bypasses by design — the bypass-primitive allowlist names this file for exactly that reason).
/// <para>
/// Scoped to ONLY the User row's own state transition; role-assignment / audit / session-revoke compose around
/// this port at the transaction-orchestration layer (task 8's wiring), each through its own already-established
/// port (<c>IRoleRepository</c>, <c>IRegistrationAuditWriter</c>, <c>ISessionStore</c>) — not reimplemented here.
/// </para>
/// </summary>
internal enum ApproveRegistrationOutcome
{
    Approved,
    AlreadyApprovedSameMerchant,
    AlreadyApprovedDifferentMerchant,
    NotFound,
    NotApprovable,
}

internal enum RejectRegistrationOutcome
{
    Rejected,
    NotFound,
    NotPending,
}

internal interface IApproveRegistrationWriter
{
    Task<ApproveRegistrationOutcome> ApproveAsync(string subject, Guid targetMerchantId, CancellationToken cancellationToken);
}

internal interface IRejectRegistrationWriter
{
    Task<RejectRegistrationOutcome> RejectAsync(string subject, CancellationToken cancellationToken);
}

internal sealed class MerchantRegistrationWriter(MerchantUserDbContext db, ISecurityTelemetry telemetry)
    : IApproveRegistrationWriter, IRejectRegistrationWriter
{
    public async Task<ApproveRegistrationOutcome> ApproveAsync(
        string subject, Guid targetMerchantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (targetMerchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(targetMerchantId));

        var affected = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Subject == subject && u.MerchantId == null && u.Status == UserStatus.PendingApproval)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.MerchantId, targetMerchantId)
                .SetProperty(u => u.Status, UserStatus.Active), cancellationToken);

        if (affected == 1)
            return ApproveRegistrationOutcome.Approved;

        Emit(targetMerchantId, "Approve", "Registration approve affected 0 rows; the row was not a pending, unbound registration.");
        var (found, status, merchantId) = await CurrentStateAsync(subject, cancellationToken);
        if (!found)
            return ApproveRegistrationOutcome.NotFound;
        if (status == UserStatus.Active)
            return merchantId == targetMerchantId
                ? ApproveRegistrationOutcome.AlreadyApprovedSameMerchant
                : ApproveRegistrationOutcome.AlreadyApprovedDifferentMerchant;
        return ApproveRegistrationOutcome.NotApprovable; // Rejected/Suspended (must resubmit first), or a raced retry
    }

    public async Task<RejectRegistrationOutcome> RejectAsync(string subject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var affected = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Subject == subject && u.MerchantId == null && u.Status == UserStatus.PendingApproval)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Rejected), cancellationToken);

        if (affected == 1)
            return RejectRegistrationOutcome.Rejected;

        Emit(TargetMerchant: null, "Reject", "Registration reject affected 0 rows; the row was not a pending, unbound registration.");
        var (found, _, _) = await CurrentStateAsync(subject, cancellationToken);
        return found ? RejectRegistrationOutcome.NotPending : RejectRegistrationOutcome.NotFound;
    }

    private async Task<(bool Found, UserStatus Status, Guid? MerchantId)> CurrentStateAsync(
        string subject, CancellationToken cancellationToken)
    {
        var row = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Subject == subject)
            .Select(u => new { u.Status, u.MerchantId })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? (false, default, null) : (true, row.Status, row.MerchantId);
    }

    private void Emit(Guid? TargetMerchant, string operation, string reason) =>
        telemetry.Emit(new DenialEvent(
            DenialCategory.PortCardinalityAnomaly, "admin", ActorId: null, TargetMerchant, nameof(User), operation,
            reason, CorrelationId.Current, DateTime.UtcNow));
}
