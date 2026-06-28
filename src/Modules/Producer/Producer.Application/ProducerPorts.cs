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
public interface ITenantUserRepository
{
    Task<TenantUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
    void Add(TenantUser user);
}

public interface IExternalLoginRepository
{
    void Add(ExternalLogin login);
}

public interface IRegistrationTicketRepository
{
    /// <summary>Single-use consume guard (REQ-3.3/4.1): a conditional UPDATE that stamps <c>UsedAt</c> only while the
    /// ticket is unused and unexpired. Returns true ONLY for the one caller whose UPDATE affected the row, so two
    /// concurrent submissions of the same ticket (2-tab) yield exactly one winner — the loser gets false (no row
    /// touched), never a replay (S9).</summary>
    Task<bool> TryConsumeAsync(Guid ticketId, TicketPurpose purpose, DateTime now, CancellationToken cancellationToken);
}

public interface ITenantUserProfileRepository
{
    Task<TenantUserProfile?> FindByTenantUserIdAsync(Guid tenantUserId, CancellationToken cancellationToken);
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
