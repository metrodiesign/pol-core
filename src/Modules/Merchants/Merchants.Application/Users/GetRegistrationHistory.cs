using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain.Users;

namespace Merchants.Application.Users;

/// <summary>
/// Admin read of one merchant user's full registration history (registration-attempt-history REQ-2/3): every
/// form snapshot ordered by attempt, plus the lifecycle timeline (submit → rejected(reason) → resubmitted →
/// approved) assembled from <c>RegistrationAudits</c>. PII is masked by default; <paramref name="Reveal"/>
/// returns full values response-wide and writes ONE <see cref="RegistrationAuditAction.Revealed"/> row per
/// request, saved BEFORE the response is built (fail-closed — precedent <c>GetOrderDetailHandler</c>: a reveal
/// that cannot be proven audited must not happen). Null result → host returns 404 (no audit, REQ-3.6).
/// </summary>
public sealed record GetRegistrationHistoryQuery(
    string Subject, bool Reveal, string ActorSubject, string CorrelationId)
    : IQuery<RegistrationHistoryResult?>;

public sealed record RegistrationHistoryResult(
    string Subject, UserStatus Status,
    IReadOnlyList<AttemptView> Attempts, IReadOnlyList<TimelineEntry> Timeline);

/// <summary>One captured submission. First/last name are always full (REQ-3.3 — the admin must identify the
/// applicant); IdNumber/LicenseNumber/Phone/Email are masked unless the request revealed.</summary>
public sealed record AttemptView(
    int AttemptNo, TicketPurpose Purpose, DateTime SubmittedAt,
    string FirstName, string LastName, PersonType? PersonType,
    string? IdNumber, string? ProducerCode, string? LicenseNumber, string? Phone,
    string Email, string? PhotoObjectKey, string? PhotoContentType);

public sealed record TimelineEntry(
    string Action, string? ActorSubject, string? Role, string? Reason,
    Guid? MerchantId, DateTime OccurredAt, string CorrelationId);

public sealed class GetRegistrationHistoryHandler
    : IQueryHandler<GetRegistrationHistoryQuery, RegistrationHistoryResult?>
{
    private readonly IAccountResolver _accounts;
    private readonly IRegistrationHistoryReader _history;
    private readonly IRegistrationAuditWriter _audits;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public GetRegistrationHistoryHandler(
        IAccountResolver accounts,
        IRegistrationHistoryReader history,
        IRegistrationAuditWriter audits,
        IUserUnitOfWork unitOfWork,
        IClock clock)
    {
        _accounts = accounts;
        _history = history;
        _audits = audits;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<RegistrationHistoryResult?> Handle(
        GetRegistrationHistoryQuery query, CancellationToken cancellationToken)
    {
        // IAccountResolver (filter-free): the target row may still carry a NULL MerchantId (pending/rejected),
        // which the ordinary query-filtered repository would hide (REQ-2.4).
        var account = await _accounts.FindBySubjectAsync(query.Subject, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return null; // host → 404; no audit of any kind (REQ-2.5/3.6)

        var attempts = await _history.ListAttemptsAsync(account.MerchantUserId, cancellationToken).ConfigureAwait(false);
        var audits = await _history.ListAuditsAsync(query.Subject, cancellationToken).ConfigureAwait(false);

        if (query.Reveal)
        {
            // Fail-closed (REQ-3.5/3.7): the revealed audit row is committed BEFORE any full PII is assembled;
            // if this save throws, the shared exception handler returns a 5xx and no PII leaves the handler.
            // Written on every revealed 200, including an empty attempts list (G2).
            _audits.Append(RegistrationAudit.For(
                RegistrationAuditAction.Revealed, query.Subject, query.CorrelationId, _clock.UtcNow,
                actorSubject: query.ActorSubject));
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var attemptViews = attempts.Select(a => new AttemptView(
            a.AttemptNo, a.Purpose, a.SubmittedAt,
            a.FirstName, a.LastName, a.PersonType,
            query.Reveal ? a.IdNumber : PiiMask.Last4(a.IdNumber),
            a.ProducerCode,
            query.Reveal ? a.LicenseNumber : PiiMask.Last4(a.LicenseNumber),
            query.Reveal ? a.Phone : PiiMask.Last4(a.Phone),
            query.Reveal ? a.Email : PiiMask.Email(a.Email)!,
            a.PhotoObjectKey, a.PhotoContentType)).ToList();

        var timeline = audits.Select(a => new TimelineEntry(
            a.Action, a.ActorSubject, a.Role, a.Reason, a.MerchantId, a.OccurredAt, a.CorrelationId)).ToList();

        return new RegistrationHistoryResult(account.Subject, account.Status, attemptViews, timeline);
    }
}

/// <summary>
/// Masking rules for the registration history (REQ-3.1/3.2). Deliberately NOT the
/// <c>Orders.Application/GetOrders.MaskIdNumber</c> shape — that one returns <c>*</c> per character for short
/// values (leaks the length); REQ-3.1 pins a constant <c>****</c> instead.
/// </summary>
internal static class PiiMask
{
    /// <summary>null → null; length &gt; 4 → <c>****</c> + last 4; length ≤ 4 → constant <c>****</c>.</summary>
    public static string? Last4(string? value) => value switch
    {
        null => null,
        { Length: > 4 } => $"****{value[^4..]}",
        _ => "****",
    };

    /// <summary>null → null; <c>local@domain</c> → first char + <c>***@</c> + full domain; no <c>@</c> (or
    /// empty local part) → constant <c>****</c> (fail-safe: mask the whole value).</summary>
    public static string? Email(string? value)
    {
        if (value is null)
            return null;
        var at = value.IndexOf('@');
        return at < 1 ? "****" : $"{value[0]}***@{value[(at + 1)..]}";
    }
}
