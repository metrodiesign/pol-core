using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Static scan gate for probe-dependency-failure-mapping's S2 seam (REQ-1.1, 1.2, 5.2): EVERY pure read
/// <c>Persistence.MerchantRuntime</c> issues on the request path must run inside
/// <c>PlatformReadGuard.ReadAsync</c> — or sit on the (file, method) allowlist below with a reason. Three
/// facts: (1) every known read token is guarded or allowlisted; (2) catch-all — an <c>*Async(</c> call on
/// the context that matches NO known token is red, forcing the token list to grow instead of silently
/// missing a new read method (the <c>ToDictionaryAsync</c>-class hole); (3) allowlist entries that no
/// longer match real code are red, so the allowlist cannot rot wider than what exists.
/// </summary>
public sealed class PlatformReadGuardCoverageTests
{
    private const string ScanRoot = "src/Persistence/Persistence.MerchantRuntime";

    /// <summary>Every EF/ADO async READ method this assembly is known to use (design "token ครบชุด").</summary>
    private static readonly string[] ReadTokens =
    [
        "ToListAsync(", "ToArrayAsync(", "ToDictionaryAsync(", "ToHashSetAsync(",
        "FirstOrDefaultAsync(", "FirstAsync(", "SingleOrDefaultAsync(", "SingleAsync(",
        "AnyAsync(", "AllAsync(", "CountAsync(", "LongCountAsync(", "MaxAsync(", "MinAsync(",
        "SumAsync(", "ContainsAsync(", "FindAsync(", "LoadAsync(", "ForEachAsync(",
        "AsAsyncEnumerable(", "ExecuteReaderAsync(", "ExecuteScalarAsync(",
    ];

    /// <summary>Write/infra async calls the guard must NEVER cover (REQ-1.5: a write whose outcome is
    /// unknown stays un-retryable) — exempt from the fact-2 catch-all so they cannot be "fixed" by
    /// wrapping them.</summary>
    private static readonly string[] NonReadTokens =
    [
        "SaveChangesAsync(", "BeginTransactionAsync(", "CommitAsync(", "RollbackAsync(",
        "OpenConnectionAsync(", "CloseConnectionAsync(", "DisposeAsync(",
    ];

    /// <summary>(file -> methods) that stay UNGUARDED, with the design's reason. "*" = whole file.</summary>
    private static readonly Dictionary<string, string[]> Allowlist = new()
    {
        // Write side — the unit of work's 3 existing translations stay untouched (REQ-1.5, REQ-5.3).
        ["src/Persistence/Persistence.MerchantRuntime/MerchantRuntimeUnitOfWork.cs"] = ["*"],
        // Reads inside the audit-chain WRITE unit (its own transaction under sp_getapplock) — the method's
        // sum is a write, not a request-path read.
        ["src/Persistence/Persistence.MerchantRuntime/Vault/VaultAuditAppender.cs"] = ["AppendAsync", "AcquireChainLockAsync"],
        // Background drain — not a request path.
        ["src/Persistence/Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs"] = ["*"],
        // NEXT VALUE FOR advances sequence state — not a pure read.
        ["src/Persistence/Persistence.MerchantRuntime/Orders/OrderNoSequence.cs"] = ["NextAsync"],
        // Read-before-write in the same idempotency unit on the webhook path.
        ["src/Persistence/Persistence.MerchantRuntime/Idempotency/EfIdempotencyStore.cs"] = ["TryBeginAsync"],
        // StoreAsync's read-before-write; Reveal/Masked/Exists ARE guarded.
        ["src/Persistence/Persistence.MerchantRuntime/Vault/LocalEnvelopeVaultStore.cs"] = ["StoreAsync"],
        // Outbox-consumer / maintenance callers — background, verified against real call sites.
        ["src/Persistence/Persistence.MerchantRuntime/Orders/DoubleSellAuditor.cs"] = ["*"],
        ["src/Persistence/Persistence.MerchantRuntime/Vault/VaultMaintenance.cs"] = ["*"],
        // Hourly bounded cleanup — background service, never request path.
        ["src/Persistence/Persistence.MerchantRuntime/AdminControlMaintenanceService.cs"] = ["*"],
        // Transaction-owned applock acquisition is synchronization infrastructure, not a data read.
        ["src/Persistence/Persistence.MerchantRuntime/Payments/PaymentAuthorizationSqlLockManager.cs"] = ["*"],
        // Operator-only expand/backfill/cutover/rollback workflow; never reachable from request endpoints.
        ["src/Persistence/Persistence.MerchantRuntime/Payments/Capabilities/PaymentCapabilityMigrationService.cs"] = ["*"],
    };

    private static readonly Regex MethodDeclaration = new(
        @"^\s*(?:public|internal|private|protected)[^=;()]*?(?<name>\w+)\s*\(", RegexOptions.Compiled);

    private static readonly Regex DbReference = new(@"(?<![\w.])_?db\.|\.Set<", RegexOptions.Compiled);
    private static readonly Regex AnyAsyncCall = new(@"\w+Async\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Fact1_every_read_token_is_guarded_or_allowlisted()
    {
        var (offenders, _) = Scan();
        Assert.True(offenders.Fact1.Count == 0,
            "Unguarded platform read on the request path — wrap it as "
            + "`await PlatformReadGuard.ReadAsync(ct => <query>.XxxAsync(..., ct), cancellationToken)` "
            + "or allowlist the (file, method) with a reason. Offenders: "
            + string.Join(", ", offenders.Fact1));
    }

    [Fact]
    public void Fact2_every_async_context_call_matches_a_known_token()
    {
        var (offenders, _) = Scan();
        Assert.True(offenders.Fact2.Count == 0,
            "Async call on the MerchantRuntime context that matches NO known read token — add the method to "
            + "ReadTokens (and guard the call) so the token list keeps up with the code, or to NonReadTokens "
            + "if it is a write/infra call the guard must never cover. Offenders: "
            + string.Join(", ", offenders.Fact2));
    }

    [Fact]
    public void Fact3_allowlist_entries_still_match_real_code()
    {
        var (_, usedEntries) = Scan();
        var stale = new List<string>();
        foreach (var (file, methods) in Allowlist)
        {
            if (!File.Exists(Path.Combine(FindRepoRoot(), file)))
            {
                stale.Add($"{file} (file gone)");
                continue;
            }
            stale.AddRange(methods
                .Where(m => !usedEntries.Contains($"{file}::{m}"))
                .Select(m => $"{file}::{m} (no matching call site)"));
        }
        Assert.True(stale.Count == 0,
            "Allowlist entry no longer matches the code — remove it so the allowlist cannot silently stay "
            + "wider than reality: " + string.Join(", ", stale));
    }

    private static ((List<string> Fact1, List<string> Fact2) Offenders, HashSet<string> UsedEntries) Scan()
    {
        var repoRoot = FindRepoRoot();
        var fact1 = new List<string>();
        var fact2 = new List<string>();
        var used = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, ScanRoot), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var lines = File.ReadAllLines(file);
            var currentMethod = "";

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var declaration = MethodDeclaration.Match(line);
                if (declaration.Success && !Regex.IsMatch(line, @"\b(class|record|interface|delegate)\b"))
                    currentMethod = declaration.Groups["name"].Value;

                var readToken = ReadTokens.FirstOrDefault(line.Contains);
                var nonReadToken = NonReadTokens.FirstOrDefault(line.Contains);

                if (readToken is not null)
                {
                    if (IsGuarded(lines, i))
                        continue;
                    if (TryUseAllowlist(relative, currentMethod, used))
                        continue;
                    fact1.Add($"{relative}:{i + 1} ({readToken.TrimEnd('(')})");
                    continue;
                }

                // fact 2 catch-all: an async call on the context that is NEITHER a known read token NOR a
                // known write/infra call. The guard's own wrapper line is exempt — ReadAsync IS the guard.
                if (nonReadToken is not null || line.Contains("PlatformReadGuard.ReadAsync"))
                {
                    TryUseAllowlist(relative, currentMethod, used); // counts as usage for fact 3
                    continue;
                }
                if (DbReference.IsMatch(line) && AnyAsyncCall.IsMatch(line))
                {
                    if (TryUseAllowlist(relative, currentMethod, used))
                        continue;
                    fact2.Add($"{relative}:{i + 1}");
                }
            }
        }

        return ((fact1, fact2), used);
    }

    /// <summary>A token is guarded when <c>PlatformReadGuard.ReadAsync(</c> appears earlier in the SAME
    /// statement — walk back to the previous statement/block boundary and search the span.</summary>
    private static bool IsGuarded(string[] lines, int tokenLine)
    {
        for (var i = tokenLine; i >= 0; i--)
        {
            // A line ABOVE the token that closes a statement/block belongs to earlier code — stop BEFORE
            // reading it, or a guarded one-liner just above would vouch for this unguarded read.
            if (i < tokenLine)
            {
                var trimmed = lines[i].TrimEnd();
                if (trimmed.EndsWith(';') || trimmed.EndsWith('{') || trimmed.EndsWith('}'))
                    return false;
            }
            if (lines[i].Contains("PlatformReadGuard.ReadAsync("))
                return true;
        }
        return false;
    }

    private static bool TryUseAllowlist(string relativeFile, string method, HashSet<string> used)
    {
        if (!Allowlist.TryGetValue(relativeFile, out var methods))
            return false;
        if (methods.Contains("*"))
        {
            used.Add($"{relativeFile}::*");
            return true;
        }
        if (methods.Contains(method))
        {
            used.Add($"{relativeFile}::{method}");
            return true;
        }
        return false;
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
