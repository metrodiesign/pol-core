using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Static scan gate for rls-to-query-filter REQ-2.12: the design's transaction inventory (28 rows — 22 from
/// the original v7 design + row 23 (task 4's ChangeAdminTier flow) + row 24 (masterdata-full-crud's
/// reference-list DeactivateAsync, one new call site per store) + rows 25-28 (merchant-user management)
/// + rows 29-31 (Governance decision and inbound-event writes))
/// (design.md §"Transaction inventory") classifies EVERY transaction API call site in the codebase as
/// single-context or (for exactly one, ProvisionMerchant) cross-context. A NEW call site that shows up here
/// without a corresponding design classification is exactly the failure mode the prior review loop hit
/// (Codex round 5: "5 MORE cross-context flows found" after the inventory was assumed complete) — this test
/// turns "a new transaction site appeared" into a red CI run instead of a missed row.
///
/// Two disjoint checks, matching how the inventory rows decompose in code:
/// 1. Every `.ExecuteInTransactionAsync(` CALL SITE (not the interface declaration or its four
///    implementations' method signatures — those have no leading `.`) — this is design rows 1-21.
/// 2. Every raw transaction primitive (`BeginTransaction[Async]`/`UseTransaction`/`TransactionScope`)
///    OUTSIDE the three known `IUnitOfWork` implementations (which use it internally to BACK
///    ExecuteInTransactionAsync — not a distinct flow) — this must be exactly
///    <c>VaultAuditAppender.cs</c> (design row 22, R1-v7 #6: opens its own transaction directly, never going
///    through the shared UoW) + <c>ProvisioningCoordinator.cs</c> (design row 16, the ONE cross-context flow).
/// </summary>
public sealed class TransactionInventoryTests
{
    // design.md rows 1-13 + 16-23 (one call site each) and rows 17-18 (two call sites in one file). Rows
    // 14-15+24 (the four reference master-data stores) are gone with those modules.
    private static readonly Dictionary<string, int> ExpectedExecuteInTransactionAsyncSites = new()
    {
        ["src/Application/Modules/Payments.Application/HandlePspWebhook/HandlePspWebhookHandler.cs"] = 1, // row 21
        // merchant-psp-settings task 8: the fetch-confirm-only (Omise) rematcher confirms a parked webhook and
        // resolves its pending rows in one transaction — single-context (txn data plane only), no admin actor.
        ["src/Application/Modules/Payments.Application/HandlePspWebhook/InboundWebhookRematcher.cs"] = 1,
        // transaction-integrity: public ConfirmAsync composes prepare outside the transaction with one
        // transaction-owned Session lock/apply; callers with an ambient transaction join it.
        ["src/Application/Modules/Payments.Application/Confirmation/PaymentConfirmationService.cs"] = 1,
        // purchase-flow-completion design.md ("Expire + mint ใหม่" -> 2-phase SaveChanges in one transaction):
        // single-context (txn data plane only). Retiring an aged-out session and minting its replacement must
        // commit together, and the UPDATE must be sent before the INSERT or the filtered unique index rejects
        // the pair — hence its own transaction rather than one batched SaveChanges. Second site (REQ-3.6,
        // review PR #167): the fresh-mint path's own transaction, so the UPDLOCK order re-read and the INSERT
        // commit together — "still AwaitingPayment" must hold at commit, not merely at the unlocked read.
        ["src/Application/Modules/Payments.Application/CreateSession/CreateSessionHandler.cs"] = 3,
        // merchant-user-payment-method-access: authorization lock + effective resolver + session write share
        // the MerchantRuntime transaction, so policy cannot change between decision and persistence.
        ["src/Application/Modules/Payments.Application/StartRedirect/StartRedirectHandler.cs"] = 1,
        // purchase-flow-completion design.md (REQ-4.7, review PR #167): single-context (txn data plane only).
        // Cancel's flip UPDATE and its post-flip re-check for a session minted behind the release must share
        // one transaction — found -> the whole cancel rolls back as a 409.
        ["src/Application/Modules/Orders.Application/CancelOrder.cs"] = 1,
        ["src/Application/Modules/Orders.Application/OrderPaidConsumer.cs"] = 1,
        ["src/Application/Modules/Orders.Application/OrderPaymentFailedConsumer.cs"] = 1,
        ["src/Application/Modules/Orders.Application/OrderPaymentExpiredConsumer.cs"] = 1,
        ["src/Application/Modules/Admins.Application/Users/UnassignMerchant.cs"] = 1,                       // row 9
        ["src/Application/Modules/Admins.Application/Users/ReactivateAdmin.cs"] = 1,                        // row 4
        ["src/Application/Modules/Admins.Application/Users/RevokeAdminSession.cs"] = 1,                     // row 5
        ["src/Application/Modules/Admins.Application/Users/SelfProvisionSuperAdmin.cs"] = 1,                // row 6
        ["src/Application/Modules/Admins.Application/Users/CreateScopedAdmin.cs"] = 1,                      // row 3
        ["src/Application/Modules/Admins.Application/Users/BindInvitedAdmin.cs"] = 1,                       // row 2
        ["src/Application/Modules/Admins.Application/Users/SuspendAdmin.cs"] = 1,                           // row 8
        ["src/Application/Modules/Admins.Application/Users/AssignMerchant.cs"] = 1,                         // row 1
        ["src/Application/Modules/Admins.Application/Users/SetAdminRoles.cs"] = 1,                          // row 7
        ["src/Application/Modules/Admins.Application/Users/ChangeAdminTier.cs"] = 1,                        // row 23 (task 4)
        ["src/Application/Modules/Admins.Application/Users/ResolveMicrosoftAdmin.cs"] = 1,                  // Tier 0 resolve/bind/JIT
        ["src/Application/Modules/Merchants.Application/Users/ApproveReject.cs"] = 2,                    // rows 17+18
        ["src/Application/Modules/Merchants.Application/Users/SubmitRegistration.cs"] = 1,               // row 20
        ["src/Application/Modules/Merchants.Application/Users/SetUserRoles.cs"] = 1,                     // row 19
        ["src/Application/Modules/Merchants.Application/Users/ManageMerchantUsers.cs"] = 4,             // rows 25-28
        ["src/Application/Modules/Iam.Application/Roles/UpdateRole.cs"] = 1,                                   // row 13
        ["src/Application/Modules/Iam.Application/Roles/DeleteRole.cs"] = 1,                                   // row 12
        ["src/Application/Modules/Iam.Application/Roles/CreateRole.cs"] = 1,                                   // row 11
        ["src/Api/Api/Orders/OrderCreationCoordinator.cs"] = 1,                                    // direct Cart-to-Order shared MerchantRuntime transaction
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Governance/GovernanceStore.cs"] = 3,              // rows 29-31
        ["src/Application/Modules/Orders.Application/OrderWorkflow.cs"] = 6, // order/user idempotent workflows
        ["src/Application/Modules/Platform.Application/Transactions/CheckoutTransactionService.cs"] = 8, // neutral cross-module transaction orchestrator
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Merchants/AdminMerchantControlStore.cs"] = 7, // rows 32-33 plus merchant master writes
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Payments/AdminPaymentsControlStore.cs"] = 16, // all Admin payment mutations lease-covered (incl. simple-routing set task 5, candidate credential test task 6, environment change task 7)
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Payments/Capabilities/EffectivePaymentCapabilityResolver.cs"] = 1, // request-scoped authorization snapshot
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Payments/Capabilities/PaymentCapabilityMigrationService.cs"] = 3, // backfill, cutover, rollback
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Payments/LegacyPaymentRemediationService.cs"] = 2, // task 9 offline remediation: env backfill + per-merchant legacy-snapshot upgrade write phase
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Payments/AdminPaymentsApprovalExecutor.cs"] = 3, // rows 38-39 + environment activation (task 7)
        ["src/Infrastructure/Persistence/Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs"] = 3, // row 40: atomic flow + recoverable claim/result
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/AgentRegistrationStore.cs"] = 4, // registration case/attempt/decision transactions
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs"] = 1, // account/access atomic identity flow
        ["src/Infrastructure/Persistence/Persistence.MerchantRuntime/Notifications/NotificationDeliveryDispatcher.cs"] = 2, // bounded delivery claim/retry transactions
        ["src/Infrastructure/Persistence/Persistence.MerchantRuntime/Notifications/NotificationMaterializer.cs"] = 1, // notification inbox/materialization transaction
        ["src/Infrastructure/Persistence/Persistence.MerchantRuntime/Notifications/NotificationOperations.cs"] = 3, // notification retry/receipt/review transactions
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Governance/ControlPlaneOperationExecutor.cs"] = 1, // row 41
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Iam/ApiClientApprovalExecutor.cs"] = 1, // row 42
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Iam/ApiClientStore.cs"] = 1, // row 43
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Notifications/DeliveryStore.cs"] = 2, // rows 44-45
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Admins/WorkforceTenantBindingStore.cs"] = 1, // boot-time tenant singleton
    };

    // The two runtime IUnitOfWork implementations plus the provisioning coordinator —
    // their internal BeginTransactionAsync backs ExecuteInTransactionAsync and is not a separate inventory row.
    private static readonly HashSet<string> KnownUnitOfWorkImplementations =
    [
        "src/Infrastructure/Persistence/Persistence.ControlPlane/Admins/ControlPlaneUnitOfWork.cs",
        "src/Infrastructure/Persistence/Persistence.MerchantRuntime/MerchantRuntimeUnitOfWork.cs",
        "src/Infrastructure/Persistence/Persistence.MerchantUsers/Users/MerchantUserRepositories.cs",
    ];

    // design row 22 (R1-v7 #6): the raw-transaction sites that do NOT go through the shared UoW.
    // VaultAuditAppender is task 6's applock-based port, wired as the live IVaultRevealAuditWriter
    // implementation since task 8's "1 principal" collapse (the old proc-based VaultRevealAuditWriter and
    // its EXECUTE AS proc are gone — REQ-1.6/task 6).
    private static readonly Dictionary<string, int> ExpectedRawTransactionSites = new()
    {
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Vault/VaultAuditAppender.cs"] = 1,
        // design row 16 (ProvisionMerchant) — task 7's ProvisioningCoordinator opens its own transaction
        // directly (shared across ControlPlaneDbContext + MerchantRuntimeDbContext, R5 #1), the ONE
        // cross-context flow. Task 8.5.4 rewired ProvisionMerchantHandler onto IProvisioningWriter, so its
        // own single-context ExecuteInTransactionAsync call site (formerly row 16) is gone — the coordinator
        // is now the ONLY transaction this flow opens.
        ["src/Infrastructure/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs"] = 1,
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/Notifications/WebhookDeliveryDispatcher.cs"] = 1, // row 46
        ["src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs"] = 2, // account/access atomic identity flows
        ["src/Infrastructure/BuildingBlocks.Infrastructure/Migration/SqlMigrationMaintenanceLease.cs"] = 1, // Task9 writer lease
        ["src/Infrastructure/BuildingBlocks.Infrastructure/Migration/SqlMigrationReadinessStore.cs"] = 2, // Task9 durable rehearsal writes
        // Tier 0 offline cutover tool: separate privileged serializable first-run and completed-verifier paths;
        // neither is referenced by API runtime.
        ["src/Infrastructure/Tools/WorkforceIdentityMigrator/Program.cs"] = 2,
    };

    private static readonly Regex ExecuteInTransactionAsyncCallSite = new(@"\.ExecuteInTransactionAsync\(", RegexOptions.Compiled);
    private static readonly Regex RawTransactionPrimitive =
        new(@"\.BeginTransaction(Async)?\(|\.UseTransaction\(|TransactionScope", RegexOptions.Compiled);

    [Fact]
    public void Every_ExecuteInTransactionAsync_call_site_matches_the_design_inventory()
    {
        var actual = ScanCounts(ExecuteInTransactionAsyncCallSite);
        AssertMatches(ExpectedExecuteInTransactionAsyncSites, actual,
            "ExecuteInTransactionAsync call site(s) not classified in design.md's transaction inventory (design rows 1-21) — "
            + "add the flow to the inventory table AND to ExpectedExecuteInTransactionAsyncSites here, classifying it "
            + "single-context or (if it touches more than one runtime context) extending the ONE sanctioned cross-context "
            + "provisioning UoW.");
    }

    [Fact]
    public void Every_raw_transaction_primitive_outside_shared_UnitOfWork_is_classified()
    {
        var actual = ScanCounts(RawTransactionPrimitive)
            .Where(kv => !KnownUnitOfWorkImplementations.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        AssertMatches(ExpectedRawTransactionSites, actual,
            "Raw transaction primitive (BeginTransaction/UseTransaction/TransactionScope) used OUTSIDE the known "
            + "IUnitOfWork implementations and classified operator/background flows — this is a NEW transaction "
            + "flow bypassing the shared UoW; classify it in design.md's transaction inventory before adding it here.");
    }

    private static void AssertMatches(Dictionary<string, int> expected, Dictionary<string, int> actual, string message)
    {
        var missing = expected.Keys.Except(actual.Keys).OrderBy(k => k).ToList();
        var unexpected = actual.Keys.Except(expected.Keys).OrderBy(k => k).ToList();
        var countMismatch = expected.Keys.Intersect(actual.Keys)
            .Where(k => expected[k] != actual[k])
            .Select(k => $"{k} (expected {expected[k]}, found {actual[k]})")
            .OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0 && unexpected.Count == 0 && countMismatch.Count == 0,
            $"{message}\nMissing (expected but not found — file moved/removed?): {string.Join(", ", missing)}"
            + $"\nUnexpected (new, unclassified): {string.Join(", ", unexpected)}"
            + $"\nCount mismatch: {string.Join(", ", countMismatch)}");
    }

    private static Dictionary<string, int> ScanCounts(Regex pattern)
    {
        var repoRoot = FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        var counts = new Dictionary<string, int>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var matchCount = pattern.Matches(File.ReadAllText(file)).Count;
            if (matchCount == 0)
                continue;

            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            counts[relative] = matchCount;
        }

        return counts;
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
