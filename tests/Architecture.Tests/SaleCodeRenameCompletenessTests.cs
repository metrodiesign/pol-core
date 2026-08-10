namespace Architecture.Tests;

/// <summary>
/// Core model/storage keeps <c>SaleCode</c>. Merchant registration deliberately uses canonical wire field
/// <c>producerCode</c>; this gate confines that compatibility spelling to the registration HTTP/DTO boundary.
/// <para>Applied migrations are excluded on purpose: a shipped migration is a frozen record of the schema as it
/// stood, and the column really was called <c>ProducerCode</c> then — including the rename migration itself,
/// which must name both spellings to do its job.</para>
/// </summary>
public sealed class SaleCodeRenameCompletenessTests
{
    private static readonly string[] Roots = ["src", "tests", "docker"];

    /// <summary>Exact registration boundary plus tests that pin its wire name.</summary>
    private static readonly string[] Allowed =
    [
        "tests/Architecture.Tests/SaleCodeRenameCompletenessTests.cs",       // this ban itself
        "src/Hosts/Api/Program.cs",                                           // multipart/request DTO wire contract
        "src/Hosts/Api/Merchants/UserRegistration.cs",                        // producerCode -> SaleCode mapper
        "src/Modules/Merchants/Merchants.Application/Users/ManageMerchantUsers.cs", // manager edit wire DTO
        "tests/Hosts.Tests/UserRegistrationFormTests.cs",                     // canonical key + legacy key rejection
        "tests/Hosts.Tests/SfsOpenApiTests.cs",                               // published multipart schema
        "tests/Hosts.Tests/RegistrationHistoryEndpointTests.cs"               // history stays saleCode
    ];

    [Fact]
    public void ProducerCode_stays_confined_to_the_registration_wire_boundary()
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
            "ProducerCode/producerCode escaped the approved registration wire boundary; core model/storage uses SaleCode. Offenders: "
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
            "Allowlisted for the registration producerCode boundary, but no longer names it — drop the entry. "
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
