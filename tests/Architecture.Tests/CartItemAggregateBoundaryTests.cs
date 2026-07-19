using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Static scan gate for rls-to-query-filter REQ-6.4: <c>Item</c> (the <c>shop.CartItems</c> row) is owned by
/// its <c>Cart</c> aggregate and must never be loaded or queried on its own — every read goes through
/// <c>Cart.Items</c> navigation (see <c>Item</c>'s own doc comment) via <c>CartRepository</c>'s
/// <c>Include(c =&gt; c.Items)</c>. <c>MerchantRuntimeDbContext</c> exposes a public
/// <c>DbSet&lt;CartItem&gt; CartItems</c> property (the same declaration shape every other aggregate root
/// gets) purely so EF can build the model and so <c>ItemConfiguration</c> can register its query filter — no
/// production code queries it directly today. This test keeps it that way: a NEW <c>.CartItems</c> call site
/// outside the DbContext's own declaration is exactly the aggregate-boundary violation REQ-6.4 forbids.
/// </summary>
public sealed class CartItemAggregateBoundaryTests
{
    private const string AllowedDeclaration = "src/Persistence/Persistence.MerchantRuntime/MerchantRuntimeDbContext.cs";

    // Negative lookbehind excludes "shop.CartItems" — the SQL schema-qualified table name that shows up in
    // migration DDL strings and doc comments, not a C# DbSet member access.
    private static readonly Regex CartItemsMemberAccess = new(@"(?<!shop)\.CartItems\b", RegexOptions.Compiled);

    [Fact]
    public void CartItems_DbSet_is_never_queried_outside_the_Cart_aggregate()
    {
        var repoRoot = FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            if (!CartItemsMemberAccess.IsMatch(File.ReadAllText(file)))
                continue;

            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != AllowedDeclaration)
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "Item/CartItem is owned by its Cart aggregate (REQ-6.4) — load it via Cart.Items navigation "
            + "(CartRepository.GetAsync's Include), never via MerchantRuntimeDbContext.CartItems directly. "
            + "Offenders: " + string.Join(", ", offenders));
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
