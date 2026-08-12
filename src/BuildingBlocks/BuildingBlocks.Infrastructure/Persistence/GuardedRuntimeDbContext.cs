using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Shared write floor for every rls-to-query-filter runtime context (<c>ControlPlaneDbContext</c>/
/// <c>MerchantUserDbContext</c>/<c>MerchantRuntimeDbContext</c>, task 3 design.md "Write floor — sealed
/// guard on EVERY runtime context"). Seals all 4 <c>SaveChanges</c> overloads through one save-core here so
/// no derived context can weaken the guard, and (paired with the contexts being <c>internal sealed</c>) no
/// write reaches the database except through this path.
/// <para>
/// Per tracked Added/Modified/Deleted entry: an <see cref="AppendOnlyDescriptor"/>-marked entity type
/// rejects Modified/Deleted outright (REQ-2.4 — an audit trail that can be edited is not an audit trail).
/// An entity carrying a <see cref="TenantKeyDescriptor"/> tenant key rejects <see cref="Guid.Empty"/>
/// (REQ-2.3, REQ-3.5 — ANY actor, including platform) and rejects any change to that key's value on
/// Modified UNLESS it is the one legitimate NULL-&gt;value transition (REQ-2.9; TenantKeyDescriptor only
/// ever allows ONE property to be nullable at all, so "the original value was null" already uniquely
/// identifies that transition — no separate carve-out flag needed). Every entry — tenant-keyed or not —
/// then goes through <see cref="IWriteAuthorizer"/> (REQ-2.11, default-deny; targetMerchant is the tenant
/// key's current value, or <see cref="Guid.Empty"/> for an entity with no tenant key of its own, e.g.
/// every ControlPlaneDbContext entity).
/// </para>
/// </summary>
public abstract class GuardedRuntimeDbContext : DbContext
{
    private readonly IWriteAuthorizer _authorizer;
    private readonly ISecurityTelemetry _telemetry;

    protected GuardedRuntimeDbContext(
        DbContextOptions options, IWriteAuthorizer authorizer, ISecurityTelemetry telemetry) : base(options)
    {
        _authorizer = authorizer;
        _telemetry = telemetry;
    }

    public sealed override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public sealed override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardPendingChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public sealed override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public sealed override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardPendingChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardPendingChanges()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
                continue;

            var entityTypeName = entry.Metadata.ClrType.Name;

            if (entry.State is EntityState.Modified or EntityState.Deleted
                && entry.Metadata.FindAnnotation(AppendOnlyDescriptor.AnnotationName) is not null)
            {
                Emit(DenialCategory.GuardDenial, entityTypeName, entry.State.ToString(), targetMerchant: null,
                    "Append-only entity rejected Modified/Deleted.");
                throw new WriteGuardException($"{entityTypeName} is append-only; {entry.State} is not allowed.");
            }

            var targetMerchant = Guid.Empty;
            if (entry.Metadata.FindAnnotation(TenantKeyDescriptor.AnnotationName)?.Value is string tenantKeyProperty)
                targetMerchant = GuardTenantKey(entry, entityTypeName, tenantKeyProperty);

            var operation = entry.State switch
            {
                EntityState.Added => WriteOperation.Insert,
                EntityState.Modified => WriteOperation.Update,
                EntityState.Deleted => WriteOperation.Delete,
                _ => throw new InvalidOperationException($"Unreachable tracked entity state '{entry.State}'."),
            };

            if (!_authorizer.CanWrite(entry.Metadata.ClrType, operation, targetMerchant))
            {
                Emit(DenialCategory.CanWriteDenial, entityTypeName, operation.ToString(), targetMerchant,
                    "IWriteAuthorizer.CanWrite denied the (entityType, operation, targetMerchant) capability.");
                throw new WriteGuardException(
                    $"Write to {entityTypeName} ({operation}, merchant {targetMerchant}) was denied.");
            }
        }
    }

    private Guid GuardTenantKey(EntityEntry entry, string entityTypeName, string tenantKeyProperty)
    {
        var property = entry.Property(tenantKeyProperty);
        var current = (Guid?)property.CurrentValue;

        if (current == Guid.Empty)
        {
            Emit(DenialCategory.EmptyOrSentinelHit, entityTypeName, entry.State.ToString(), targetMerchant: null,
                "Tenant key was Guid.Empty on a tracked write.");
            throw new WriteGuardException($"{entityTypeName}.{tenantKeyProperty} cannot be Guid.Empty.");
        }

        if (entry.State == EntityState.Modified)
        {
            var original = (Guid?)property.OriginalValue;
            var allowNullToValue = entry.Metadata.FindAnnotation(TenantKeyDescriptor.AllowNullToValueAnnotationName)?.Value
                is true;
            var isPendingApprovalTransition = allowNullToValue && original is null && current is not null;
            if (original != current && !isPendingApprovalTransition)
            {
                Emit(DenialCategory.GuardDenial, entityTypeName, "Update", current,
                    "Tenant key is immutable after insert outside the NULL->value pending-approval carve-out.");
                throw new WriteGuardException(
                    $"{entityTypeName}.{tenantKeyProperty} is immutable after insert (was {original}, now {current}).");
            }
        }

        return current ?? Guid.Empty;
    }

    private void Emit(DenialCategory category, string entity, string operation, Guid? targetMerchant, string reason) =>
        _telemetry.Emit(new DenialEvent(
            category, GetType().Name, ActorId: null, targetMerchant, entity, operation, reason,
            CorrelationId.Current, DateTime.UtcNow));
}
