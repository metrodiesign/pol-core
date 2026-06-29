using BuildingBlocks.Application;
using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Persistence seams for the producer identity realm (registration/correction/approval). Every one binds the keyed
/// pol_admin <see cref="ProducerDbContext"/> — registration writes a NULL-tenant Pending row the RLS BLOCK
/// predicate would reject under a tenant principal (REQ-19.2). They share ONE keyed-Scoped context instance per
/// request, so a handler that stages across several of them commits in ONE transaction.
/// </summary>
public interface IProducerAccountRepository
{
    Task<ProducerAccount?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
    /// <summary>Tracked lookup by id — the per-request session re-resolution (REQ-12.4/17.1) and the admin
    /// approve/reject target load (REQ-6) both find the account by the id the session/command carries.</summary>
    Task<ProducerAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(ProducerAccount account);
}

/// <summary>The tenant edge of a <see cref="ProducerAccount"/> (REQ-6), control-plane on the keyed pol_admin context.
/// A producer acts for exactly one tenant — uniqueness on ProducerAccountId is enforced by the DB index, so a second
/// assignment for the same account raises a unique-violation (surfaced as 409).</summary>
public interface IProducerTenantAssignmentRepository
{
    Task<ProducerTenantAssignment?> FindByAccountIdAsync(Guid producerAccountId, CancellationToken cancellationToken);
    void Add(ProducerTenantAssignment assignment);
}

public interface IExternalLoginRepository
{
    void Add(ExternalLogin login);
}

public interface IRegistrationTicketRepository
{
    /// <summary>Stages a new server-side <c>RegistrationTickets</c> row — the single-use replay authority a signed
    /// wire ticket is backed by (REQ-3.4). Minted at the callback for the NotFound (Registration) / Rejected
    /// (Correction) branches; persisted on the keyed pol_admin context.</summary>
    void Add(RegistrationTicket ticket);

    /// <summary>Single-use consume guard (REQ-3.3/4.1): a conditional UPDATE that stamps <c>UsedAt</c> only while the
    /// ticket is unused and unexpired. Returns true ONLY for the one caller whose UPDATE affected the row, so two
    /// concurrent submissions of the same ticket (2-tab) yield exactly one winner — the loser gets false (no row
    /// touched), never a replay (S9).</summary>
    Task<bool> TryConsumeAsync(Guid ticketId, TicketPurpose purpose, DateTime now, CancellationToken cancellationToken);
}

public interface ITenantUserProfileRepository
{
    Task<TenantUserProfile?> FindByProducerAccountIdAsync(Guid producerAccountId, CancellationToken cancellationToken);
    void Add(TenantUserProfile profile);
}

/// <summary>Append-only writer for <c>RegistrationAudits</c> (REQ-21) on the keyed pol_admin context.</summary>
public interface IRegistrationAuditWriter
{
    void Append(RegistrationAudit audit);
}

/// <summary>
/// Enqueues an integration event onto the SAME keyed pol_admin <see cref="ProducerDbContext"/> as the registration
/// write so the row + the event commit atomically (REQ-20.2, critique B1). NOT the stock <c>IOutbox</c>/<c>EfOutbox</c>,
/// which bind the default pol_app context and throw without a bound tenant — registration runs tenant-less. The row
/// is stamped with a fixed non-empty platform/sentinel tenant id (Guid.Empty is rejected downstream).
/// </summary>
public interface IProducerOutboxWriter
{
    void Enqueue(INotification notification);
}

/// <summary>
/// The transactional seam for the registration write (REQ-4.1/20.2). <see cref="ExecuteInTransactionAsync{T}"/> runs
/// the ticket-consume + the inserts + the outbox enqueue in ONE pol_admin transaction; <see cref="SaveChangesAsync"/>
/// translates a unique-violation (duplicate subject / (provider,subject) race) into a <c>ConflictException</c> → 409,
/// never a 500 (S9). Bound to the keyed pol_admin context.
/// </summary>
public interface IProducerRegistrationUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Generic control-plane commit seam over the keyed pol_admin <c>ProducerDbContext</c> for the role/assignment
/// (REQ-16) and approve/reject (REQ-6) writes — a neutral name so those handlers do not read as "registration",
/// though the same implementing class backs both. Bound keyed pol_admin in the API (role/catalog/identity tables are
/// control-plane, RLS-bypass); bound to the DEFAULT context in the worker ONLY so the Mediator-discovered handlers'
/// dependency graphs RESOLVE under ValidateOnBuild (the worker never sends those commands). Translates a
/// unique-violation to a 409 (duplicate role code race) — never a 500.
/// </summary>
public interface IProducerUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Records the Admin-side "awaiting approval" notice idempotently (REQ-20.4). Bound to the DEFAULT
/// <see cref="ProducerDbContext"/> — in the worker that is the pol_worker connection (the outbox dispatcher
/// principal, granted INSERT/SELECT on the control-plane notice table). Idempotent on TenantUserId so a redelivered
/// event records nothing twice and never poisons the message.
/// </summary>
public interface IProducerRegistrationNoticeWriter
{
    Task<bool> ExistsAsync(Guid tenantUserId, CancellationToken cancellationToken);
    void Add(ProducerRegistrationNotice notice);
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
}
