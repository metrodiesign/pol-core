using System.Text.RegularExpressions;

namespace Architecture.Tests;

public sealed class Tier0WorkforceArchitectureTests
{
    [Fact]
    public void Retired_pre_provision_surface_is_absent_from_production_source()
    {
        var root = FindRepoRoot();
        var production = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        foreach (var retired in new[]
                 {
                     "PreProvisionMicrosoftIdentity",
                     "IAdminIdentityAuditWriter",
                     "/{id:guid}/microsoft-identity",
                     // retire-legacy-admin-identity-plane: the whole legacy admin identity plane is gone.
                     "WorkforceIdentityMigrator",
                     "IWorkforceTenantBindingStore",
                     "VerifyActiveSuperAsync",
                 })
        {
            Assert.DoesNotContain(production, text => text.Contains(retired, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Production_compose_configures_microsoft_without_the_retired_google_provider()
    {
        var root = FindRepoRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.prod.yml"));

        Assert.Contains("IdentityAccess__Workforce__ClientId", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminAuth__", compose, StringComparison.Ordinal);
        Assert.Contains("ADMIN_ENTRA_CLIENT_ID:?", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminAuth__Providers__Google", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("MerchantAuth__Providers__Google", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("admin_oidc_client_secret", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("merchant_user_oidc_client_secret", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Workforce_email_key_token_is_confined_to_immutable_migration_compatibility_artifacts()
    {
        var root = FindRepoRoot();
        var allowed = new[]
        {
            "src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/20260823132337_Tier0WorkforceEmailIdentity.cs",
            "src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/20260823132337_Tier0WorkforceEmailIdentity.Designer.cs",
            "src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/20260830172117_Tier0EmployeeProfile.Designer.cs",
            "src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/20260902133906_Tier0MicrosoftTenantAwareIdentity.cs",
        };
        var actual = ProductionSources(root)
            .Where(path => File.ReadAllText(path).Contains("WorkforceEmailKey", StringComparison.Ordinal))
            .Select(path => Relative(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowed.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Current_test_tree_contains_no_committed_skip_directive()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .Where(path => SkippedTestPattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Relative(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static readonly Regex SkippedTestPattern = new(
        @"\[(?:Fact|Theory)\s*\([^\]]*\bSki" + @"p\s*=|Assert\.Ski" + @"p(?:When|Unless)?\s*\(|Ski"
        + "pException|Ski" + "ppableFact",
        RegexOptions.CultureInvariant);

    private static IEnumerable<string> ProductionSources(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path));

    private static bool IsBuildArtifact(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
