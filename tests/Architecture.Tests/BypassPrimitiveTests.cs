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
        "src/Persistence/Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs", // task 8.5.3 mirror of the old BuildingBlocks.Infrastructure OutboxDispatcher (moved)
        "src/Persistence/Persistence.MerchantRuntime/Webhooks/WebhookMerchantResolver.cs", // task 8.5.3 mirror of the old BuildingBlocks.Infrastructure WebhookMerchantResolver (moved)
        "src/Persistence/Persistence.MerchantRuntime/Vault/VaultAuditAppender.cs", // task 6 applock-based vault-audit chain append (replaces sec.usp_vault_audit_head)
        "src/Persistence/Persistence.MerchantRuntime/Orders/OrderSummaryReader.cs", // task 8.5.3 mirror of the old Orders.Infrastructure OrderSummaryReader (moved)
        "src/Persistence/Persistence.MerchantRuntime/Orders/OrderNoSequence.cs", // purchase-flow-completion task 6: NEXT VALUE FOR shop.OrderNoSeq (IOrderNoSequence) — EF has no sequence primitive; one statement, no entity, no predicate to widen
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantAccountResolver.cs", // bugfix-merchant-prebind-wiring: pre-bind login-by-subject/by-id read (IAccountResolver, narrow projection)
        "src/Persistence/Persistence.MerchantUsers/Outbox/MerchantUserOutboxDrain.cs", // task 5 per-owner outbox drain (cross-owner lease scan)
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantAccountStore.cs", // bugfix-merchant-prebind-wiring: pre-bind tracked target load for registration/correction/approve/reject (IAccountStore) — read-filter bypass only, the write floor still authorizes every staged change
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantUserSessionStore.cs", // task 8.5.2 mirror of the old Merchants.Infrastructure SessionStore (deleted)
        "src/Persistence/Persistence.MerchantUsers/MerchantRoleAssignmentCountReader.cs", // task 8.5.2 cross-merchant role-assignment count (mirrors IRoleAssignmentCounter)
        "src/Persistence/Persistence.MerchantUsers/MerchantRoleAssignmentReader.cs", // task 8.5.7 cross-context role-id read for HostMerchantRoleRepository (explicit merchantId param, not ambient state)
        "src/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs", // task 7 provisioning UoW: UPDLOCK/HOLDLOCK authz recheck + idempotency-ledger raw INSERT (named-index conflict detection)
        "src/Persistence/Persistence.MerchantRuntime/Payments/Psp/ConnectionRepository.cs", // task 8.5.8: ListByTenantAsync is GetMerchantHandler's admin cross-merchant read-back (explicit merchantId param, its only caller)
        "src/Persistence/Persistence.MerchantRuntime/Orders/Items/AdminItemPolicyWriter.cs", // policy-reference-record task 5: admin cross-merchant ItemPolicy write escape-hatch (IAdminItemPolicyWriter) — Scoped admin confined by AdminItemPolicyWriteAuthorizer.CanWrite checking IAdminScope.Accessible, never widened here
        "src/Persistence/Persistence.MerchantRuntime/Orders/Items/PolicyReportSfs.cs", // policy-reference-record task 6: admin cross-merchant policy-report read escape-hatch (IAdminItemPolicyReader, via BuildQuery's IgnoreQueryFilters) — confined by IsUnrestrictedAdmin/AccessibleMerchantIds + optional ?merchantId=, never widened here
        "src/Persistence/Persistence.MerchantRuntime/Payments/PayableOrderReader.cs", // purchase-flow-completion REQ-3.6 (review PR #167, fixed PR #168): GetForMintAsync's UPDLOCK re-read is a raw scalar Database.SqlQueryRaw with no merchant predicate — the row is single by Id and the merchant floor is enforced by the GetAsync LINQ read that follows it, inside the same transaction
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
