using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Static scan gate for rls-to-query-filter REQ-5.2/REQ-11.4: bypass primitives that go around the change
/// tracker + sealed write guard (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>) or the read filter
/// (<c>IgnoreQueryFilters</c>/<c>SqlQueryRaw</c>/<c>FromSql*</c>/<c>ExecuteSql*</c>/<c>GetDbConnection</c>)
/// are banned everywhere in <c>src/</c> except a named allowlist of narrow, single-purpose operation ports
/// (design.md §"Escape-hatch allowlist" — each file below IS exactly one such port: session
/// revoke/prune, outbox lease, webhook merchant resolution, the vault-audit head read, the public
/// order-summary token read). A NEW call site outside the allowlist is exactly the "universal bypass"
/// REQ-5.6 forbids — this turns it into a red CI run instead of a silent widening of the escape hatch.
/// </summary>
public sealed class BypassPrimitiveTests
{
    // Every file that currently calls a bypass primitive — each is a narrow, single-purpose port (not a
    // generic gateway): session-family revoke/prune (ExecuteUpdate/ExecuteDeleteAsync), the outbox
    // dispatcher's lease query, the PSP-webhook merchant resolver, the vault-audit head reader, and the
    // public order-summary token reader (all SqlQueryRaw against a stored procedure/parameterized SQL).
    private static readonly HashSet<string> AllowedPorts =
    [
        "src/Persistence/Persistence.ControlPlane/Admins/SessionStore.cs", // task 8.5.1 mirror of the old Admins.Infrastructure SessionStore (deleted)
        "src/Persistence/Persistence.ControlPlane/Admins/WorkforceTenantBindingStore.cs", // Tier 0 startup: read-only singleton migration state query; user invariant reads remain normal filtered EF queries
        "src/Persistence/Persistence.ControlPlane/Admins/EmployeeProfileReader.cs", // admin-employee-profile-sync task 1: one read-only parameterized SELECT TOP (2) of EmpCode/FirstNameTh/LastNameTh from operator-managed dbo.VibEmp; no writable EF entity
        "src/Persistence/Persistence.ControlPlane/Governance/GovernanceSqlLockManager.cs", // admin-console Task 2: transaction-owned applock + audit-head row lock, both constrained by explicit resource/scope key
        "src/Persistence/Persistence.ControlPlane/Governance/GovernanceOutboxDispatcher.cs", // admin-console Task 2: READPAST/UPDLOCK lease of a bounded pending Governance outbox batch
        "src/Persistence/Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs", // task 8.5.3 mirror of the old BuildingBlocks.Infrastructure OutboxDispatcher (moved)
        "src/Persistence/Persistence.MerchantRuntime/Webhooks/WebhookMerchantResolver.cs", // task 8.5.3 mirror of the old BuildingBlocks.Infrastructure WebhookMerchantResolver (moved)
        "src/Persistence/Persistence.MerchantRuntime/Vault/VaultAuditAppender.cs", // task 6 applock-based vault-audit chain append (replaces sec.usp_vault_audit_head)
        "src/Persistence/Persistence.MerchantRuntime/Orders/OrderSummaryReader.cs", // task 8.5.3 mirror of the old Orders.Infrastructure OrderSummaryReader (moved)
        "src/Persistence/Persistence.MerchantRuntime/Orders/OrderNoSequence.cs", // purchase-flow-completion task 6: NEXT VALUE FOR shop.OrderNoSeq (IOrderNoSequence) — EF has no sequence primitive; one statement, no entity, no predicate to widen
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantAccountResolver.cs", // bugfix-merchant-prebind-wiring: pre-bind login-by-subject/by-id read (IAccountResolver, narrow projection)
        "src/Persistence/Persistence.MerchantUsers/Outbox/MerchantUserOutboxDrain.cs", // task 5 per-owner outbox drain (cross-owner lease scan)
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantAccountStore.cs", // bugfix-merchant-prebind-wiring: pre-bind tracked target load for registration/correction/approve/reject (IAccountStore) — read-filter bypass only, the write floor still authorizes every staged change
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantInvitationRepository.cs", // merchant real-API invitation flow: exact hash/id pre-bind reads only; caller rechecks pending/expiry/email before tenant binding
        "src/Persistence/Persistence.MerchantUsers/Users/AdminMerchantUserReader.cs", // admin-console Task 5: explicit Admin scope set constrains every cross-merchant user/role read
        "src/Persistence/Persistence.MerchantUsers/Users/AdminUserOperationStore.cs", // admin-console Task 5: exact merchant/actor/operation/idempotency-key replay lookup; append-only writes remain guarded
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantUserSessionStore.cs", // task 8.5.2 mirror of the old Merchants.Infrastructure SessionStore (deleted)
        "src/Persistence/Persistence.MerchantUsers/MerchantRoleAssignmentCountReader.cs", // task 8.5.2 cross-merchant role-assignment count (mirrors IRoleAssignmentCounter)
        "src/Persistence/Persistence.MerchantUsers/MerchantRoleAssignmentReader.cs", // task 8.5.7 cross-context role-id read for HostMerchantRoleRepository (explicit merchantId param, not ambient state)
        "src/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs", // task 7 provisioning UoW: UPDLOCK/HOLDLOCK authz recheck + idempotency-ledger raw INSERT (named-index conflict detection)
        "src/Persistence/Persistence.MerchantRuntime/Payments/Psp/ConnectionRepository.cs", // task 8.5.8: ListByTenantAsync is GetMerchantHandler's admin cross-merchant read-back (explicit merchantId param, its only caller)
        "src/Persistence/Persistence.MerchantRuntime/Orders/DocumentSaleProbe.cs", // products-external-source-of-truth REQ-5.2: the sold-check MUST see across merchants — a document another merchant already sold is still sold — so the read floor is bypassed BY DESIGN, not worked around. Narrow: 3 projected columns, no writes, keyed by an explicit DocumentNo list the caller passes in (no ambient state to widen). Deliberately does NOT emit ISecurityTelemetry (design finding S5): none of the 11 pinned DenialCategory values means "an intentional cross-tenant read", and this port runs on every catalogue search, add-item, checkout and payment-session mint — emitting there would bury the real denial signal under constant noise, unlike ConnectionRepository.ListByTenantAsync which is a rare admin read.
        "src/Persistence/Persistence.MerchantRuntime/Payments/PayableOrderReader.cs", // merchant-commerce-erd-reset REQ-9.14: GetForMintAsync serializes payment-attempt minting with a narrow UPDLOCK/HOLDLOCK scalar read constrained by explicit Order Id + ambient MerchantId; GetAsync then projects the merchant-filtered row inside the same transaction
        "src/Persistence/Persistence.MerchantRuntime/Orders/OrderRepository.cs", // merchant-commerce-erd-reset REQ-9.14/9.18: GetForUpdateAsync serializes every Order lifecycle writer with a narrow UPDLOCK/HOLDLOCK scalar read constrained by explicit Order Id + ambient MerchantId before loading the tracked aggregate
        "src/Persistence/Persistence.MerchantRuntime/Merchants/MerchantRepository.cs", // admin provisioning/read-back directory: every cross-merchant read is constrained by explicit code/id/id-set; IAdminQuery applies Scoped access before full projection
        "src/Persistence/Persistence.MerchantRuntime/Merchants/AdminMerchantControlStore.cs", // admin-console Task 4: explicit Admin scope set on every merchant/originator read; writes require matching tenant
        "src/Persistence/Persistence.MerchantRuntime/Payments/AdminPaymentsControlStore.cs", // admin-console Task 4: explicit Admin scope set; PSP/routing operations remain tenant-keyed
        "src/Persistence/Persistence.MerchantRuntime/Vault/LocalEnvelopeVaultStore.cs", // admin-console Task 4: exact merchantId + versionId/name predicates for staged credential versions
        "src/Persistence/Persistence.MerchantRuntime/AdminControlMaintenanceService.cs", // admin-console Task 4: bounded background expiry cleanup; no request input
        "src/Persistence/Persistence.MerchantRuntime/Orders/DoubleSellAuditor.cs", // products-external-source-of-truth REQ-5.16: the double-sell audit MUST see across merchants — the second buyer of a document is very often ANOTHER company, so a merchant-filtered read would report nothing exactly when there IS a double sale — so the read floor is bypassed BY DESIGN. Narrow: a read-only report, no writes; two reads keyed by the order's own id and an explicit DocumentNo list, joined to Paid orders. Runs once per order that becomes Paid (not per request), so unlike DocumentSaleProbe it is a rare event — but it emits no ISecurityTelemetry for the same reason (an intentional cross-tenant read is no DenialCategory) and only ever LogCritical on a real double sale.
        "src/Persistence/Persistence.MerchantRuntime/Carts/AdminCartReader.cs", // admin-console Task 6: read-only cross-merchant cart projection constrained by explicit cart id plus Admin merchant scope
        "src/Persistence/Persistence.MerchantRuntime/Orders/AdminOrderReader.cs", // admin-console Task 6: read-only order list/detail constrained by explicit Admin access set and optional merchant id
        "src/Persistence/Persistence.MerchantRuntime/Payments/AdminPaymentSessionReader.cs", // admin-console Task 6: pre-bind reads constrained by explicit session/order/connection id and expected merchant
        "src/Persistence/Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs", // admin-console Task 6: exact merchant + actor + operation + idempotency-key ledger lookup; writes remain guarded
        "src/Persistence/Persistence.MerchantRuntime/Reporting/AdminReportingReader.cs", // admin-console Task 7: read-only transaction/report projection constrained by explicit Admin merchant scope
        "src/Persistence/Persistence.MerchantRuntime/Payments/InboundWebhookStore.cs", // admin-console Task 8: sanitized event queries constrained by explicit Admin merchant scope; claims remain connection/event keyed
        "src/Persistence/Persistence.ControlPlane/Notifications/WebhookDeliveryDispatcher.cs", // admin-console Task 8: bounded background lease claim constrained to pending/expired-processing rows
        "src/Persistence/Persistence.ControlPlane/Payments/PaymentAuthorizationSqlLockManager.cs", // merchant-user-payment-method-access: transaction-owned global applock used only by migration cutover
        "src/Persistence/Persistence.MerchantRuntime/Payments/PaymentAuthorizationSqlLockManager.cs", // merchant-user-payment-method-access: transaction-owned global/merchant applocks with explicit merchant key
        "src/Persistence/Persistence.MerchantRuntime/Payments/Capabilities/EffectivePaymentCapabilityResolver.cs", // canonical resolver: exact subject, merchant, method, provider and account predicates
        "src/Persistence/Persistence.MerchantRuntime/Payments/Capabilities/PaymentCapabilityMigrationService.cs", // operator-only deterministic backfill/cutover/rollback under exclusive global lock
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantUserRepositories.cs", // merchant-user UoW: transaction-owned payment-authorization applock with explicit merchant key
    ];

    private static readonly Regex BypassPrimitive = new(
        @"\.ExecuteUpdate(Async)?\(|\.ExecuteDelete(Async)?\(|\.IgnoreQueryFilters\(|\.SqlQueryRaw|\.FromSql(Raw|Interpolated)?\(|\.ExecuteSql(Raw|Interpolated)?(Async)?\(|\.GetDbConnection\(",
        RegexOptions.Compiled);

    [Fact]
    public void Bypass_primitives_are_used_only_by_the_allowlisted_narrow_ports()
    {
        var repoRoot = FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            if (!BypassPrimitive.IsMatch(File.ReadAllText(file)))
                continue;

            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!AllowedPorts.Contains(relative))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "Bypass primitive (ExecuteUpdate/ExecuteDelete/IgnoreQueryFilters/SqlQueryRaw/FromSql*/ExecuteSql*/"
            + "GetDbConnection) used outside the allowlisted narrow ports — classify it as a named operation port "
            + "with a tenant/target/state WHERE predicate and add it to AllowedPorts, or route the write through "
            + "IWriteAuthorizer instead. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_allowlisted_port_still_exists_and_still_uses_a_bypass_primitive()
    {
        var repoRoot = FindRepoRoot();
        var stale = AllowedPorts
            .Where(relative => !BypassPrimitive.IsMatch(File.ReadAllText(Path.Combine(repoRoot, relative))))
            .ToList();

        Assert.True(stale.Count == 0,
            "Allowlisted port no longer uses a bypass primitive (moved/refactored?) — shrink AllowedPorts so the "
            + "allowlist does not silently widen beyond what is actually in use: " + string.Join(", ", stale));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pol-core.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate repo root (pol-core.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
