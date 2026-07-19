using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Static scan gate for rls-to-query-filter task 5 ("no <c>Find</c>/<c>FindAsync</c> on merchant-scoped
/// entities"): <c>DbSet&lt;T&gt;.Find</c>/<c>FindAsync</c> checks the change tracker's LOCAL cache first and
/// returns a cache hit WITHOUT re-running the query (and therefore WITHOUT re-applying the global query
/// filter) — if a foreign-tenant entity is already tracked in the same context instance for any reason, a
/// same-key <c>Find</c> silently hands it back, undermining REQ-1's "the filter is an unconditional floor"
/// guarantee. Banned everywhere under <c>src/Persistence/</c> (the three runtime-context assemblies) — every
/// lookup there must be an explicit <c>Where(...)</c> query, which always re-applies the filter. No allowlist:
/// unlike <see cref="BypassPrimitiveTests"/>'s escape hatches, there is no legitimate reason to use the
/// cache-shortcut form inside a runtime context, so a new call site is always a bug, not a port to classify.
/// </summary>
public sealed class RuntimeContextFindBanTests
{
    private static readonly Regex FindPrimitive = new(@"\.Find(Async)?\(", RegexOptions.Compiled);

    [Fact]
    public void No_runtime_context_file_calls_Find_or_FindAsync()
    {
        var repoRoot = FindRepoRoot();
        var persistenceRoot = Path.Combine(repoRoot, "src", "Persistence");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(persistenceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            if (FindPrimitive.IsMatch(File.ReadAllText(file)))
                offenders.Add(Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
        }

        Assert.True(offenders.Count == 0,
            "DbSet.Find/FindAsync used inside a runtime-context assembly (src/Persistence/) — its change-tracker "
            + "cache shortcut can skip the global query filter for an already-tracked foreign-tenant entity. "
            + "Replace with an explicit Where(...) lookup instead. Offenders: " + string.Join(", ", offenders));
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
