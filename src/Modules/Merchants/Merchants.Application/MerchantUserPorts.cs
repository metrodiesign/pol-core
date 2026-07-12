using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;

namespace Merchants.Application;

/// <summary>
/// Persistence seams for the merchant-user identity realm (registration/correction/approval). Every one binds the keyed
/// pol_admin <see cref="PolDbContext"/> — registration writes a NULL-merchant Pending row the RLS BLOCK
/// predicate would reject under a merchant principal (REQ-19.2). They share ONE keyed-Scoped context instance per
/// request, so a handler that stages across several of them commits in ONE transaction.
/// </summary>
public interface IMerchantUserRepository
{
    Task<MerchantUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
    /// <summary>Tracked lookup by id — the per-request session re-resolution (REQ-12.4/17.1) and the admin
    /// approve/reject target load (REQ-6) both find the account by the id the session/command carries.</summary>
    Task<MerchantUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(MerchantUser account);
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
/// Enqueues an integration event onto the SAME keyed pol_admin <see cref="PolDbContext"/> as the registration
/// write so the row + the event commit atomically (REQ-20.2, critique B1). NOT the stock <c>IOutbox</c>/<c>EfOutbox</c>,
/// which bind the default pol_app context and throw without a bound merchant — registration runs merchant-less. The row
/// is stamped with a fixed non-empty platform/sentinel merchant id (Guid.Empty is rejected downstream).
/// </summary>
public interface IMerchantsOutboxWriter
{
    void Enqueue(INotification notification);
}

/// <summary>
/// The transactional seam for the registration write (REQ-4.1/20.2). <see cref="ExecuteInTransactionAsync{T}"/> runs
/// the ticket-consume + the inserts + the outbox enqueue in ONE pol_admin transaction; <see cref="SaveChangesAsync"/>
/// translates a unique-violation (duplicate subject / (provider,subject) race) into a <c>ConflictException</c> → 409,
/// never a 500 (S9). Bound to the keyed pol_admin context.
/// </summary>
public interface IMerchantsRegistrationUnitOfWork
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
public interface IMerchantsUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Records the Admin-side "awaiting approval" notice idempotently (REQ-20.4). Bound to the DEFAULT
/// <see cref="PolDbContext"/> — in the worker that is the pol_worker connection (the outbox dispatcher
/// principal, granted INSERT/SELECT on the control-plane notice table). Idempotent on MerchantUserId so a redelivered
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
}
