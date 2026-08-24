using System.Text.RegularExpressions;

namespace Architecture.Tests;

public sealed partial class Tier0WorkforceArchitectureTests
{
    [Fact]
    public void Tier0_production_path_has_no_role_or_object_identifier_gate()
    {
        var root = FindRepoRoot();
        var files = new[]
        {
            "src/Hosts/Api/Admins/MicrosoftWorkforceClaims.cs",
            "src/Hosts/Api/Admins/OidcAuthentication.cs",
            "src/Hosts/Api/Admins/LoginService.cs",
        };
        var source = string.Join('\n', files.Select(path => File.ReadAllText(Path.Combine(root, path))));

        Assert.DoesNotContain("vcp.employee", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(ForbiddenClaimRead(), source);
        Assert.DoesNotContain("JitProvisionMicrosoftAdminCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Retired_pre_provision_surface_is_absent_from_production_source()
    {
        var root = FindRepoRoot();
        var production = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.DoesNotContain(production, text => text.Contains("PreProvisionMicrosoftIdentity", StringComparison.Ordinal));
        Assert.DoesNotContain(production, text => text.Contains("IAdminIdentityAuditWriter", StringComparison.Ordinal));
        Assert.DoesNotContain(production, text => text.Contains("/{id:guid}/microsoft-identity", StringComparison.Ordinal));
    }

    [Fact]
    public void Migration_tool_is_privileged_and_not_referenced_by_api()
    {
        var root = FindRepoRoot();
        var apiProject = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/Api.csproj"));
        var tool = File.ReadAllText(Path.Combine(root, "src/Tools/WorkforceIdentityMigrator/Program.cs"));

        Assert.DoesNotContain("WorkforceIdentityMigrator", apiProject, StringComparison.Ordinal);
        Assert.Contains("POL_DESIGN_SQL", tool, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", tool, StringComparison.Ordinal);
        Assert.Contains("admin-user-identity-mutation", tool, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception.Message", tool, StringComparison.Ordinal);
        Assert.DoesNotContain("connectionString}", tool, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_compose_configures_admin_microsoft_without_retired_google_provider()
    {
        var root = FindRepoRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.prod.yml"));

        Assert.Contains("AdminAuth__Providers__Microsoft__ClientId", compose, StringComparison.Ordinal);
        Assert.Contains("ADMIN_ENTRA_CLIENT_ID:?", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminAuth__Providers__Google", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("admin_oidc_client_secret", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Tier0_catch_paths_never_pass_exception_objects_to_logger()
    {
        var root = FindRepoRoot();
        var login = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/Admins/LoginService.cs"));

        Assert.DoesNotMatch(LogExceptionObject(), login);
    }

    [GeneratedRegex("Find(?:All|First)\\s*\\(\\s*\"(?:roles|oid)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenClaimRead();

    [GeneratedRegex("Log(?:Error|Warning|Information)\\s*\\(\\s*(?:ex|exception)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex LogExceptionObject();

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
