namespace Architecture.Tests;

/// <summary>
/// products-external-source-of-truth REQ-10.4 — the old <c>ProducerCode</c>/<c>producerCode</c> spelling is gone
/// from live code, tests and the bootstrap/seed scripts. Two names for one thing is exactly what the rename set
/// out to remove, and a leftover is not always a compile error: the wire spellings live in string literals
/// (form keys, SQL column lists), which the repo's rename gate deliberately strips before matching and therefore
/// cannot see. A plain text scan is the only thing that does.
/// <para>Applied migrations are excluded on purpose: a shipped migration is a frozen record of the schema as it
/// stood, and the column really was called <c>ProducerCode</c> then — including the rename migration itself,
/// which must name both spellings to do its job.</para>
/// </summary>
public sealed class SaleCodeRenameCompletenessTests
{
    private static readonly string[] Roots = ["src", "tests", "docker"];

    /// <summary>The only files allowed to spell the retired name: the tests whose whole job is to prove it is
    /// retired, which cannot do that without naming it. Each pins one half of the rename — the wire contract in
    /// both directions (REQ-10.7) and the column rename that carries the data over (REQ-10.2). Nothing else
    /// belongs here; a production file needing an exception means the rename is not finished.</summary>
    private static readonly string[] Allowed =
    [
        "tests/Architecture.Tests/SaleCodeRenameCompletenessTests.cs",       // this ban itself
        "tests/Hosts.Tests/UserRegistrationFormTests.cs",                    // the old form key binds nothing
        "tests/Hosts.Tests/RegistrationHistoryEndpointTests.cs"              // the old JSON key is absent
    ];

    [Fact]
    public void No_file_outside_the_migration_history_still_says_ProducerCode()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var root in Roots)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, root), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/") || relative.Contains("/Migrations/"))
                    continue;
                if (Allowed.Contains(relative, StringComparer.Ordinal))
                    continue;

                if (File.ReadAllText(file).Contains("roducerCode", StringComparison.Ordinal)) // both casings
                    offenders.Add(relative);
            }

        Assert.True(offenders.Count == 0,
            "The retired ProducerCode/producerCode spelling is still present — the field is called SaleCode "
            + "everywhere now, on the wire and in the database (REQ-10.4). Offenders: "
            + string.Join(", ", offenders));
    }

    // An allowlist entry that no longer names the retired spelling has stopped pinning anything, and leaving it
    // there quietly widens the ban's blind spot (same rule the IgnoreQueryFilters allowlist lives under).
    [Fact]
    public void Every_allowlisted_file_still_names_the_retired_spelling()
    {
        var repoRoot = FindRepoRoot();

        var stale = Allowed
            .Where(relative => !File.ReadAllText(Path.Combine(repoRoot, relative))
                .Contains("roducerCode", StringComparison.Ordinal))
            .ToList();

        Assert.True(stale.Count == 0,
            "Allowlisted for naming the retired ProducerCode spelling, but no longer does — drop the entry. "
            + string.Join(", ", stale));
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
