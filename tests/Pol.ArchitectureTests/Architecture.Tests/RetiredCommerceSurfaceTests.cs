using Iam.Domain.Permissions;

namespace Architecture.Tests;

/// <summary>REQ-8/13.12-13.13: retired Policy implementation stays physically absent while the
/// canonical Checkout capability remains present, and IAM vocabulary remains exactly 25 keys across 7 groups.</summary>
public sealed class RetiredCommerceSurfaceTests
{
    private static readonly string[] ForbiddenProductionTokens =
    [
        "ItemPolicy",
        "OrderItemPolicies",
        "OrderItemPolicyAudits",
        "PolicyReport",
        "merchants.policies",
        "policies.read",
        "policies.write",
    ];

    [Fact]
    public void Production_tree_has_no_retired_policy_surface_reference()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Select(path => new { Path = path, Text = File.ReadAllText(path) })
            .Where(file => ForbiddenProductionTokens.Any(file.Text.Contains))
            .Select(file => Path.GetRelativePath(root, file.Path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Retired Policy reference remains in production tree: " + string.Join(", ", offenders));
        Assert.True(File.Exists(Path.Combine(root,
                "src/Pol.Application/Modules/Checkouts.Application/CheckoutConfirm.cs")),
            "The canonical Checkout application surface must remain present while Policy is retired.");
    }

    [Fact]
    public void Permission_catalog_is_exactly_25_keys_and_7_groups_without_retired_policy_keys()
    {
        Assert.Equal(25, Keys.AllKeys.Count);
        Assert.Equal(7, Keys.GroupKeys.Count);
        Assert.DoesNotContain(Keys.AllKeys, key => key.Contains("policies", StringComparison.Ordinal));
        Assert.DoesNotContain(Keys.GroupKeys, key => key.Contains("policies", StringComparison.Ordinal));
    }

    [Fact]
    public void Order_model_has_no_checkout_link()
    {
        Assert.Null(typeof(Orders.Domain.Order).GetProperty("CheckoutSessionId"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pol-core.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
