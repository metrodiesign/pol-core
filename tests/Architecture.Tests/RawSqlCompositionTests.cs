namespace Architecture.Tests;

/// <summary>
/// Static gate for a money-path failure the test pyramid structurally cannot see: composing LINQ over
/// NON-composable raw SQL.
/// <para>EF wraps <c>Single*/First*/Any/Count/Where/Select</c> over a <c>SqlQueryRaw</c>/<c>FromSqlRaw</c>
/// root in an outer <c>SELECT</c>, which is only legal when the raw SQL is a single composable
/// <c>SELECT</c>. A multi-statement batch (any <c>DECLARE</c>/<c>EXEC</c> — an applock, a proc call) is not,
/// so EF throws <i>"'FromSql' or 'SqlQuery' was called with non-composable SQL"</i> while GENERATING the
/// query — before the database is ever contacted. It is a 100% failure, not a race.</para>
/// <para>This shipped once, in <c>VaultAuditAppender</c>'s <c>sp_getapplock</c> call, and no suite caught
/// it: the unit suites run SQLite (where <c>IsSqlServer()</c> is false and the applock branch is skipped),
/// and the integration suite cannot reach the port at all — it is <c>internal</c>, so
/// <c>VaultAuditAppenderIntegrationTests</c> re-issues the identical SQL through raw ADO instead of through
/// EF. Every PSP charge reveals a vault secret, and every reveal appends to that audit chain, so the effect
/// was that no payment could start on SQL Server while every test stayed green.</para>
/// The rule: raw SQL carrying <c>DECLARE</c>/<c>EXEC</c> must be materialized (<c>ToListAsync</c> /
/// <c>AsEnumerable</c>) before anything composes over it.
/// </summary>
public sealed class RawSqlCompositionTests
{
    private static readonly string[] RawSqlEntryPoints = ["SqlQueryRaw", "FromSqlRaw", "FromSqlInterpolated"];

    /// <summary>Terminals that execute the raw SQL as written — no outer SELECT is generated.</summary>
    private static readonly string[] Materializers = [".ToListAsync(", ".ToList(", ".AsEnumerable(", ".AsAsyncEnumerable("];

    /// <summary>Operators EF turns into an outer SELECT over the raw SQL (the composition that breaks).</summary>
    private static readonly string[] ComposingOperators =
    [
        ".SingleAsync(", ".SingleOrDefaultAsync(", ".FirstAsync(", ".FirstOrDefaultAsync(",
        ".Single(", ".SingleOrDefault(", ".First(", ".FirstOrDefault(",
        ".AnyAsync(", ".CountAsync(", ".Where(", ".Select(", ".OrderBy(", ".OrderByDescending(",
    ];

    /// <summary>How far past the call site to look; a raw-SQL statement never runs longer than this.</summary>
    private const int StatementWindow = 2000;

    [Fact]
    public void Non_composable_raw_sql_is_materialized_before_anything_composes_over_it()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var text = File.ReadAllText(file);

            // Only multi-statement SQL is at risk; a plain SELECT composes fine and several ports rely on that.
            if (!text.Contains("DECLARE ", StringComparison.Ordinal) && !text.Contains("EXEC ", StringComparison.Ordinal))
                continue;

            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

            foreach (var entryPoint in RawSqlEntryPoints)
            {
                for (var at = text.IndexOf(entryPoint, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(entryPoint, at + 1, StringComparison.Ordinal))
                {
                    var window = text.Substring(at, Math.Min(StatementWindow, text.Length - at));
                    var materializedAt = FirstIndexOfAny(window, Materializers);
                    var composedAt = FirstIndexOfAny(window, ComposingOperators);

                    if (composedAt >= 0 && (materializedAt < 0 || composedAt < materializedAt))
                        offenders.Add($"{relative}: {entryPoint} composes over non-composable SQL " +
                                      $"— materialize with ToListAsync/AsEnumerable first");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static int FirstIndexOfAny(string haystack, string[] needles)
    {
        var best = -1;
        foreach (var needle in needles)
        {
            var at = haystack.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best))
                best = at;
        }

        return best;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root from the test binary.");
    }
}
