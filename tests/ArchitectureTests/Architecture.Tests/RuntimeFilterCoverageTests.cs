using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;

namespace Architecture.Tests;

public sealed class RuntimeFilterCoverageTests
{
    // ControlPlane tenant-keyed entities that intentionally carry NO deny-default query filter because
    // privileged governance/notification read paths must read across merchants. Each is instead guarded
    // by an explicit Scope(...)/ApplyAccess(...) (or post-read authorize) at every read site — Governance
    // via GovernanceStore, Notifications via DeliveryStore. This allowlist is the record of that intent:
    // adding a new tenant-keyed ControlPlane entity without a filter AND without justifying it here fails
    // the test below, so the gap cannot be introduced silently the way Commerce is guarded by EF filters.
    // Keyed by ClrType.FullName (not the short name) so an entity in a different namespace that happens
    // to share a short name cannot inherit an allowlist slot it was never granted.
    private static readonly IReadOnlyDictionary<string, string> ControlPlaneCrossMerchantReads =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Governance.Domain.ApprovalRequest"] = "Maker-checker queue is read by admins across merchants via GovernanceStore.ApplyAccess.",
            ["Governance.Domain.ApprovalEvent"] = "Approval audit trail read alongside ApprovalRequest under the same governance access.",
            ["Governance.Domain.OperationRecord"] = "Governance operation ledger authorized after read in GovernanceStore.",
            ["Governance.Domain.AuditHead"] = "Audit head is an admin-facing cross-merchant ledger authorized in GovernanceStore.",
            ["Governance.Domain.AuditRecord"] = "Audit detail read with AuditHead under the same governance access.",
            ["Governance.Domain.GovernanceOutboxMessage"] = "Governance outbox drained by a platform dispatcher, not a merchant principal.",
            ["Notifications.Domain.WebhookEndpoint"] = "Webhook registry administered across merchants via GovernanceStore.ApplyAccess.",
            ["Notifications.Domain.WebhookDelivery"] = "Webhook delivery log read with WebhookEndpoint under the same access.",
            ["Notifications.Domain.NotificationDelivery"] = "Delivery log scoped explicitly by DeliveryStore.Scope at every read site.",
            ["Notifications.Domain.NotificationRule"] = "Notification rules scoped explicitly by DeliveryStore.Scope at every read site.",
            ["Notifications.Domain.DeliverySecretVersion"] = "Delivery secret versions scoped explicitly by DeliveryStore.Scope at every read site.",
        };

    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Every_commerce_tenant_keyed_entity_has_a_deny_default_query_filter()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new CommerceDbContext(
            new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(connection).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        var missing = db.Model.GetEntityTypes()
            .Where(entity => entity.FindAnnotation(TenantKeyDescriptor.AnnotationName) is not null)
            .Where(entity => entity.GetDeclaredQueryFilters().Count == 0)
            .Select(entity => entity.ClrType.FullName ?? entity.ClrType.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Every_control_plane_tenant_keyed_entity_has_a_filter_or_is_a_declared_cross_merchant_read()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        var unfiltered = db.Model.GetEntityTypes()
            .Where(entity => entity.FindAnnotation(TenantKeyDescriptor.AnnotationName) is not null)
            .Where(entity => entity.GetDeclaredQueryFilters().Count == 0)
            .Select(entity => entity.ClrType.FullName ?? entity.ClrType.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unguarded = unfiltered
            .Where(name => !ControlPlaneCrossMerchantReads.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(unguarded);

        // No stale allowlist entry: every declared cross-merchant read must still be a tenant-keyed
        // control-plane entity that carries no query filter. An entry whose entity gained a filter or
        // was removed becomes dead weight, and this assertion turns it red.
        var stale = ControlPlaneCrossMerchantReads.Keys
            .Where(name => !unfiltered.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(stale);
    }
}
