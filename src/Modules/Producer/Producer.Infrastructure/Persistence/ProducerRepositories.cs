using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using Mediator;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Producer.Application;
using Producer.Domain;

namespace Producer.Infrastructure.Persistence;

/// <summary>The fixed, non-empty platform tenant id stamped on a tenant-less registration outbox row (REQ-20.2).
/// Registration runs on pol_admin with no tenant; the dispatcher re-binds this id as SESSION_CONTEXT before
/// invoking the consumer, which therefore touches only control-plane tables. A constant marker, never a real
/// tenant (tenant ids are random GUIDs).</summary>
public static class ProducerOutbox
{
    public static readonly Guid SentinelTenantId = new("f0f0f0f0-0000-4000-8000-00000000ad17");
}

/// <summary>TenantUser reads/writes on the keyed pol_admin context (REQ-19.2). Tracked so a correction can mutate
/// the loaded aggregate before commit.</summary>
public sealed class TenantUserRepository : ITenantUserRepository
{
    private readonly ProducerDbContext _db;
    public TenantUserRepository(ProducerDbContext db) => _db = db;

    public Task<TenantUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        _db.Set<TenantUser>().FirstOrDefaultAsync(u => u.Subject == subject, cancellationToken);

    public Task<TenantUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<TenantUser>().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public void Add(TenantUser user) => _db.Set<TenantUser>().Add(user);
}

public sealed class ExternalLoginRepository : IExternalLoginRepository
{
    private readonly ProducerDbContext _db;
    public ExternalLoginRepository(ProducerDbContext db) => _db = db;

    public void Add(ExternalLogin login) => _db.Set<ExternalLogin>().Add(login);
}

/// <summary>Single-use ticket consume via a conditional set-based UPDATE (REQ-3.3/4.1): exactly one caller's UPDATE
/// affects the row, so a replay or a 2-tab race resolves to one winner. Enlists in the handler's ambient pol_admin
/// transaction (same shared context).</summary>
public sealed class RegistrationTicketRepository : IRegistrationTicketRepository
{
    private readonly ProducerDbContext _db;
    public RegistrationTicketRepository(ProducerDbContext db) => _db = db;

    public void Add(RegistrationTicket ticket) => _db.Set<RegistrationTicket>().Add(ticket);

    public async Task<bool> TryConsumeAsync(Guid ticketId, TicketPurpose purpose, DateTime now, CancellationToken cancellationToken)
    {
        var affected = await _db.Set<RegistrationTicket>()
            .Where(t => t.Id == ticketId && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now), cancellationToken);
        return affected == 1;
    }
}

public sealed class TenantUserProfileRepository : ITenantUserProfileRepository
{
    private readonly ProducerDbContext _db;
    public TenantUserProfileRepository(ProducerDbContext db) => _db = db;

    public Task<TenantUserProfile?> FindByTenantUserIdAsync(Guid tenantUserId, CancellationToken cancellationToken) =>
        _db.Set<TenantUserProfile>().FirstOrDefaultAsync(p => p.TenantUserId == tenantUserId, cancellationToken);

    public void Add(TenantUserProfile profile) => _db.Set<TenantUserProfile>().Add(profile);
}

public sealed class RegistrationAuditWriter : IRegistrationAuditWriter
{
    private readonly ProducerDbContext _db;
    public RegistrationAuditWriter(ProducerDbContext db) => _db = db;

    public void Append(RegistrationAudit audit) => _db.Set<RegistrationAudit>().Add(audit);
}

/// <summary>
/// Writes the registration integration event onto the SAME keyed pol_admin context as the write (REQ-20.2, B1),
/// stamped with <see cref="ProducerOutbox.SentinelTenantId"/>. Mirrors <c>EfOutbox</c>'s row shape (CLR simple
/// name + camelCase JSON) so the existing dispatcher deserialises it unchanged — but bypasses EfOutbox's bound-tenant
/// requirement, which registration (tenant-less) cannot satisfy.
/// </summary>
// ponytail: a focused writer, NOT the stock EfOutbox — EfOutbox binds the default pol_app context and throws without
// a bound tenant, so it can neither share this pol_admin transaction nor run tenant-less. Deliberate.
public sealed class ProducerOutboxWriter : IProducerOutboxWriter
{
    private readonly ProducerDbContext _db;
    private readonly IClock _clock;

    public ProducerOutboxWriter(ProducerDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Enqueue(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var type = notification.GetType();
        var payload = JsonSerializer.Serialize(notification, type, OutboxSerializer.Options);
        _db.OutboxMessages.Add(
            OutboxMessage.Create(Guid.CreateVersion7(), ProducerOutbox.SentinelTenantId, type.Name, payload, _clock.UtcNow));
    }
}

/// <summary>
/// Unit of work over the keyed pol_admin connection for the registration write (REQ-4.1/20.2). Clears the tracker at
/// the start of each transaction attempt (so a retried attempt restages cleanly) and translates a unique-violation
/// into a <c>ConflictException</c> (409) — a duplicate subject racing past the create is a conflict, not a 500 (S9).
/// Mirrors the Admin provisioning UoW.
/// </summary>
public sealed class ProducerRegistrationUnitOfWork : IProducerRegistrationUnitOfWork, IProducerUnitOfWork
{
    private readonly ProducerDbContext _db;
    public ProducerRegistrationUnitOfWork(ProducerDbContext db) => _db = db;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(
                "A concurrent change to the same record was detected; the save was rejected.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // SQL Server 2627/2601 = unique-violation: a duplicate subject / (provider,subject) that raced past
            // the create -> a domain conflict (409), never an opaque 500 (REQ-4.6/S9).
            throw new ConflictException(
                "A registration already exists for this identity.", ex);
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear(); // each attempt starts clean (the conditional ticket consume rolls back with the tx)
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await operation(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// Idempotent notice writer on the DEFAULT context (REQ-20.4) — in the worker that is the pol_worker connection.
/// <see cref="TrySaveAsync"/> swallows a unique-violation as an already-recorded no-op so a concurrent redelivery
/// cannot poison the dispatcher.
/// </summary>
public sealed class ProducerRegistrationNoticeWriter : IProducerRegistrationNoticeWriter
{
    private readonly ProducerDbContext _db;
    public ProducerRegistrationNoticeWriter(ProducerDbContext db) => _db = db;

    public Task<bool> ExistsAsync(Guid tenantUserId, CancellationToken cancellationToken) =>
        _db.Set<ProducerRegistrationNotice>().AsNoTracking().AnyAsync(n => n.TenantUserId == tenantUserId, cancellationToken);

    public void Add(ProducerRegistrationNotice notice) => _db.Set<ProducerRegistrationNotice>().Add(notice);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // A concurrent redelivery already recorded the notice — idempotent no-op, not a failure. Detach ONLY the
            // losing notice (NOT the whole tracker — the dispatcher shares this scoped context and its tracked
            // OutboxMessage must survive so MarkProcessed persists and the message is not redelivered forever).
            foreach (var entry in _db.ChangeTracker.Entries<ProducerRegistrationNotice>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }
}
