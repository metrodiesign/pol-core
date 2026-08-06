namespace Integration.Tests;

/// <summary>
/// Shared sqlcmd-script plumbing for the suites that replay a bootstrap script for real
/// (<see cref="SeedDemoIntegrationTests"/>, <see cref="SimSeedFixture"/>): locate the file above the
/// test output directory and split it on <c>GO</c> separators the way sqlcmd does — ADO.NET executes
/// one batch at a time and does not understand <c>GO</c> itself. Hoisted out of
/// SeedDemoIntegrationTests when SimSeedFixture became its second caller (sim-seed-date-stability),
/// so there is exactly one implementation of "run a bootstrap script from a test".
/// </summary>
internal static class SqlScripts
{
    /// <summary>Splits a sqlcmd script on its <c>GO</c> batch separators (a line that is only
    /// <c>GO</c>).</summary>
    public static IEnumerable<string> SplitBatches(string script)
    {
        var current = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = string.Join('\n', current).Trim();
                if (batch.Length > 0) yield return batch;
                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }
        var tail = string.Join('\n', current).Trim();
        if (tail.Length > 0) yield return tail;
    }

    /// <summary>Resolves a repo-relative path (e.g. <c>docker/bootstrap/seed-demo.sql</c>) by walking
    /// up from the test output directory until the file exists.</summary>
    public static string RepoPath(params string[] segments)
    {
        var relative = Path.Combine(segments);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} above the test output directory.");
    }
}
