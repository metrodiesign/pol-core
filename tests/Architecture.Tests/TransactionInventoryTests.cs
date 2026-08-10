using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Static scan gate for rls-to-query-filter REQ-2.12: the design's transaction inventory (28 rows — 22 from
/// the original v7 design + row 23 (task 4's ChangeAdminTier flow) + row 24 (masterdata-full-crud's
/// reference-list DeactivateAsync, one new call site per store) + rows 25-28 (merchant-user management))
/// (design.md §"Transaction inventory") classifies EVERY transaction API call site in the codebase as
/// single-context or (for exactly one, ProvisionMerchant) cross-context. A NEW call site that shows up here
/// without a corresponding design classification is exactly the failure mode the prior review loop hit
/// (Codex round 5: "5 MORE cross-context flows found" after the inventory was assumed complete) — this test
/// turns "a new transaction site appeared" into a red CI run instead of a missed row.
///
/// Two disjoint checks, matching how the 24 rows actually decompose in code:
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
    // design.md rows 1-13 + 16-23 (one call site each), rows 17-18 (two call sites in one file), and
    // rows 14-15+24 (three call sites in one file, the four reference master-data stores).
    private static readonly Dictionary<string, int> ExpectedExecuteInTransactionAsyncSites = new()
    {
        ["src/Modules/Payments/Payments.Application/HandlePspWebhook/HandlePspWebhookHandler.cs"] = 1, // row 21
        // purchase-flow-completion design.md ("Expire + mint ใหม่" -> 2-phase SaveChanges in one transaction):
        // single-context (txn data plane only). Retiring an aged-out session and minting its replacement must
        // commit together, and the UPDATE must be sent before the INSERT or the filtered unique index rejects
        // the pair — hence its own transaction rather than one batched SaveChanges. Second site (REQ-3.6,
        // review PR #167): the fresh-mint path's own transaction, so the UPDLOCK order re-read and the INSERT
        // commit together — "still AwaitingPayment" must hold at commit, not merely at the unlocked read.
        ["src/Modules/Payments/Payments.Application/CreateSession/CreateSessionHandler.cs"] = 2,
        // purchase-flow-completion design.md (REQ-4.7, review PR #167): single-context (txn data plane only).
        // Cancel's flip UPDATE and its post-flip re-check for a session minted behind the release must share
        // one transaction — found -> the whole cancel rolls back as a 409.
        ["src/Modules/Orders/Orders.Application/CancelOrder.cs"] = 1,
        ["src/Modules/Orders/Orders.Application/OrderPaidConsumer.cs"] = 1,
        ["src/Modules/Orders/Orders.Application/OrderPaymentFailedConsumer.cs"] = 1,
        ["src/Modules/Orders/Orders.Application/OrderPaymentExpiredConsumer.cs"] = 1,
        ["src/Modules/Admins/Admins.Application/Users/UnassignMerchant.cs"] = 1,                       // row 9
        ["src/Modules/Admins/Admins.Application/Users/ReactivateAdmin.cs"] = 1,                        // row 4
        ["src/Modules/Admins/Admins.Application/Users/RevokeAdminSession.cs"] = 1,                     // row 5
        ["src/Modules/Admins/Admins.Application/Users/SelfProvisionSuperAdmin.cs"] = 1,                // row 6
        ["src/Modules/Admins/Admins.Application/Users/UpdateAdminProfile.cs"] = 1,                     // row 10
        ["src/Modules/Admins/Admins.Application/Users/CreateScopedAdmin.cs"] = 1,                      // row 3
        ["src/Modules/Admins/Admins.Application/Users/BindInvitedAdmin.cs"] = 1,                       // row 2
        ["src/Modules/Admins/Admins.Application/Users/SuspendAdmin.cs"] = 1,                           // row 8
        ["src/Modules/Admins/Admins.Application/Users/AssignMerchant.cs"] = 1,                         // row 1
        ["src/Modules/Admins/Admins.Application/Users/SetAdminRoles.cs"] = 1,                          // row 7
        ["src/Modules/Admins/Admins.Application/Users/ChangeAdminTier.cs"] = 1,                        // row 23 (task 4)
        ["src/Modules/Merchants/Merchants.Application/Users/ApproveReject.cs"] = 2,                    // rows 17+18
        ["src/Modules/Merchants/Merchants.Application/Users/SubmitRegistration.cs"] = 1,               // row 20
        ["src/Modules/Merchants/Merchants.Application/Users/SetUserRoles.cs"] = 1,                     // row 19
        ["src/Modules/Merchants/Merchants.Application/Users/ManageMerchantUsers.cs"] = 4,             // rows 25-28
        ["src/Modules/Iam/Iam.Application/Roles/UpdateRole.cs"] = 1,                                   // row 13
        ["src/Modules/Iam/Iam.Application/Roles/DeleteRole.cs"] = 1,                                   // row 12
        ["src/Modules/Iam/Iam.Application/Roles/CreateRole.cs"] = 1,                                   // row 11
        ["src/Hosts/Api/Orders/OrderCreationCoordinator.cs"] = 1,                                    // direct Cart-to-Order shared MerchantRuntime transaction
        ["src/Persistence/Persistence.ControlPlane/Divisions/DivisionStore.cs"] = 3,                   // rows 14+15+24 (masterdata-split typed split + masterdata-full-crud DeactivateAsync)
        ["src/Persistence/Persistence.ControlPlane/Levels/LevelStore.cs"] = 3,                         // rows 14+15+24
        ["src/Persistence/Persistence.ControlPlane/Offices/OfficeStore.cs"] = 3,                       // rows 14+15+24
        ["src/Persistence/Persistence.ControlPlane/Positions/PositionStore.cs"] = 3,                   // rows 14+15+24
    };

    // The three IUnitOfWork implementations (task 8.5's "1 principal" collapse — one per runtime cluster) —
    // their internal BeginTransactionAsync backs ExecuteInTransactionAsync and is not a separate inventory row.
    private static readonly HashSet<string> KnownUnitOfWorkImplementations =
    [
        "src/Persistence/Persistence.ControlPlane/Admins/ControlPlaneUnitOfWork.cs",
        "src/Persistence/Persistence.MerchantRuntime/MerchantRuntimeUnitOfWork.cs",
        "src/Persistence/Persistence.MerchantUsers/Users/MerchantUserRepositories.cs",
    ];

    // design row 22 (R1-v7 #6): the raw-transaction sites that do NOT go through the shared UoW.
    // VaultAuditAppender is task 6's applock-based port, wired as the live IVaultRevealAuditWriter
    // implementation since task 8's "1 principal" collapse (the old proc-based VaultRevealAuditWriter and
    // its EXECUTE AS proc are gone — REQ-1.6/task 6).
    private static readonly Dictionary<string, int> ExpectedRawTransactionSites = new()
    {
        ["src/Persistence/Persistence.MerchantRuntime/Vault/VaultAuditAppender.cs"] = 1,
        // design row 16 (ProvisionMerchant) — task 7's ProvisioningCoordinator opens its own transaction
        // directly (shared across ControlPlaneDbContext + MerchantRuntimeDbContext, R5 #1), the ONE
        // cross-context flow. Task 8.5.4 rewired ProvisionMerchantHandler onto IProvisioningWriter, so its
        // own single-context ExecuteInTransactionAsync call site (formerly row 16) is gone — the coordinator
        // is now the ONLY transaction this flow opens.
        ["src/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs"] = 1,
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
    public void The_only_raw_transaction_primitive_outside_the_shared_UnitOfWork_is_VaultRevealAuditWriter()
    {
        var actual = ScanCounts(RawTransactionPrimitive)
            .Where(kv => !KnownUnitOfWorkImplementations.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        AssertMatches(ExpectedRawTransactionSites, actual,
            "Raw transaction primitive (BeginTransaction/UseTransaction/TransactionScope) used OUTSIDE the four known "
            + "IUnitOfWork implementations and outside VaultRevealAuditWriter (design row 22) — this is a NEW transaction "
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
