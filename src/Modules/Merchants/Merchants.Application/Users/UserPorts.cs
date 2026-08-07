using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;

namespace Merchants.Application.Users;

/// <summary>
/// Persistence seams for the merchant-user identity realm (registration/correction/approval). They share ONE
/// Scoped context instance per request, so a handler that stages across several of them commits in ONE transaction.
/// <para>
/// BOUND in-session call sites ONLY (e.g. <c>SetUserRoles</c>): the runtime adapter runs under the ordinary
/// merchant query filter (<c>MerchantId == CurrentMerchant</c>), so a caller without a bound merchant actor —
/// or looking at a NULL-<c>MerchantId</c> pending/rejected row — sees nothing. Pre-bind flows (login
/// resolution, registration/correction, admin approve/reject) MUST use <see cref="IAccountResolver"/> /
/// <see cref="IAccountStore"/> instead (bugfix-merchant-prebind-wiring F1/F2/F3/F4/F6).
/// </para>
/// </summary>
public interface IUserRepository
{
    Task<User?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(User account);
}

/// <summary>The narrow pre-bind projection of a merchant-user account — never the tracked aggregate.
/// <paramref name="SaleCode"/> rides along because the catalogue search picks its upstream sale code from the
/// authenticated account, never from the client (REQ-4.8); it is optional so the pre-bind flows that do not
/// care (registration, approval) keep constructing this without it.</summary>
public sealed record AccountSnapshot(
    Guid UserId, string Subject, string Email, Guid? MerchantId, UserStatus Status,
    string? SaleCode = null, string? DisplayName = null);

/// <summary>
/// Filter-free, read-only account resolution for the flows that run BEFORE any merchant actor exists on the
/// request: the OIDC callback's login-by-subject branch (<c>ResolveLogin</c>, F1) and the session auth
/// handler's per-request re-resolution by id (<c>ResolveById</c>, F6 — it runs DURING authentication, before
/// the merchant_id claim is set). Pending/Rejected rows carry a NULL <c>MerchantId</c>, so the ordinary
/// query-filtered repository can never serve these call sites; the adapter is a sanctioned
/// <c>IgnoreQueryFilters()</c> escape-hatch port (Architecture.Tests bypass allowlist).
/// </summary>
public interface IAccountResolver
{
    Task<AccountSnapshot?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
    Task<AccountSnapshot?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Filter-free TRACKED account load + add for the pre-bind WRITE flows: registration/correction submission
/// (<c>SubmitRegistration</c>, F2 — anonymous, ticket-gated) and the admin approve/reject target load
/// (<c>ApproveReject</c>, F3/F4 — an admin request has no merchant actor either). The handlers mutate the
/// aggregate through its domain methods and commit via their unit of work; the write floor
/// (<c>IWriteAuthorizer</c>) still authorizes every staged change at SaveChanges.
/// </summary>
public interface IAccountStore
{
    Task<User?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
    void Add(User account);
}

public interface IExternalLoginRepository
{
    void Add(ExternalLogin login);
}

/// <summary>Append-only writer for <c>RegistrationAudits</c> (REQ-21) on the keyed pol_admin context.</summary>
public interface IRegistrationAuditWriter
{
    void Append(RegistrationAudit audit);
}

/// <summary>
/// Append-only writer for the per-submit form snapshots (registration-attempt-history REQ-1), on the same
/// context as the registration write so the snapshot commits in the submit transaction. A race on
/// <see cref="NextAttemptNoAsync"/> is settled by the DB unique (UserId, AttemptNo) index → 409 via
/// the unit of work (REQ-1.9), not by locking here.
/// </summary>
public interface IRegistrationAttemptWriter
{
    /// <summary>max(AttemptNo)+1 for the user; 1 when no attempt exists yet.</summary>
    Task<int> NextAttemptNoAsync(Guid merchantUserId, CancellationToken cancellationToken);
    void Add(RegistrationAttempt attempt);
}

/// <summary>
/// Read side of the admin registration-history endpoint (registration-attempt-history REQ-2). Both reads are
/// AsNoTracking — the handler saves a reveal audit on the same scoped context in the reveal branch, and must
/// not accidentally flush tracked history rows with it. Neither table is merchant-filtered, so no
/// filter-free escape hatch is involved (the USER lookup goes through <see cref="IAccountResolver"/>).
/// </summary>
public interface IRegistrationHistoryReader
{
    /// <summary>All attempts for the user, ORDER BY AttemptNo ascending (REQ-2.2).</summary>
    Task<IReadOnlyList<RegistrationAttempt>> ListAttemptsAsync(Guid merchantUserId, CancellationToken cancellationToken);

    /// <summary>Lifecycle timeline rows for the subject, ORDER BY OccurredAt ascending, EXCLUDING
    /// <see cref="RegistrationAuditAction.Revealed"/> — reveal records access, not lifecycle, and keeping it
    /// would make every reveal grow the unpaginated timeline it returns (REQ-2.3/M2).</summary>
    Task<IReadOnlyList<RegistrationAudit>> ListAuditsAsync(string targetSubject, CancellationToken cancellationToken);
}

/// <summary>
/// Enqueues an integration event onto the SAME keyed pol_admin <see cref="PolDbContext"/> as the registration
/// write so the row + the event commit atomically (REQ-20.2, critique B1). NOT the stock <c>IOutbox</c>/<c>EfOutbox</c>,
/// which bind the default pol_app context and throw without a bound merchant — registration runs merchant-less. The row
/// is stamped with a fixed non-empty platform/sentinel merchant id (Guid.Empty is rejected downstream).
/// </summary>
public interface IRegistrationOutboxWriter
{
    void Enqueue(INotification notification);
}

/// <summary>
/// The transactional seam for the registration write (REQ-4.1/20.2). <see cref="ExecuteInTransactionAsync{T}"/> runs
/// the ticket-consume + the inserts + the outbox enqueue in ONE pol_admin transaction; <see cref="SaveChangesAsync"/>
/// translates a unique-violation (duplicate subject / (provider,subject) race) into a <c>ConflictException</c> → 409,
/// never a 500 (S9). Bound to the keyed pol_admin context.
/// </summary>
public interface IRegistrationUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Generic control-plane commit seam over the keyed pol_admin <c>PolDbContext</c> for the role/assignment
/// (REQ-16) and approve/reject (REQ-6) writes — a neutral name so those handlers do not read as "registration",
/// though the same implementing class backs both. Bound keyed pol_admin in the API (role/catalog/identity tables are
/// control-plane, RLS-bypass); bound to the DEFAULT context in the worker ONLY so the Mediator-discovered handlers'
/// dependency graphs RESOLVE under ValidateOnBuild (the worker never sends those commands). Translates a
/// unique-violation to a 409 (duplicate role code race) — never a 500.
/// </summary>
public interface IUserUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Records the Admin-side "awaiting approval" notice idempotently (REQ-20.4). Bound to the DEFAULT
/// <see cref="PolDbContext"/> — in the worker that is the pol_worker connection (the outbox dispatcher
/// principal, granted INSERT/SELECT on the control-plane notice table). Idempotent on UserId so a redelivered
/// event records nothing twice and never poisons the message.
/// </summary>
public interface IRegistrationNoticeWriter
{
    Task<bool> ExistsAsync(Guid merchantUserId, CancellationToken cancellationToken);
    void Add(RegistrationNotice notice);
    /// <summary>Persists staged notices; treats a unique-violation as an already-recorded no-op (returns false)
    /// rather than throwing, so a concurrent redelivery does not poison the dispatcher.</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Stores an uploaded photo's bytes OUTSIDE the database and returns an opaque, server-generated object key
/// (REQ-7.2/7.5). The dev adapter writes a gitignored directory; prod swaps an S3/Blob adapter behind this port.
/// </summary>
public interface IPhotoStore
{
    Task<string> PutAsync(byte[] bytes, string contentType, CancellationToken cancellationToken);
    Task<(byte[] Bytes, string ContentType)?> GetAsync(string objectKey, CancellationToken cancellationToken);
    Task<string> PutStagedAsync(
        Guid operationId,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        CancellationToken cancellationToken);
    Task CommitAsync(string objectKey, CancellationToken cancellationToken);
    Task DiscardStagedAsync(string objectKey, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}
