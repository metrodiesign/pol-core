using System.Text.RegularExpressions;
using Admins.Domain.Users;
using Microsoft.Data.SqlClient;
using WorkforceIdentityMigrator;

namespace Architecture.Tests;

public sealed partial class Tier0WorkforceArchitectureTests
{
    [Fact]
    public void Tier0_production_path_requires_tenant_object_claims_without_mutable_identity_fallbacks()
    {
        var root = FindRepoRoot();
        var claims = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/Admins/MicrosoftWorkforceClaims.cs"));
        var resolver = File.ReadAllText(Path.Combine(
            root, "src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs"));
        var files = new[]
        {
            "src/Hosts/Api/Admins/MicrosoftWorkforceClaims.cs",
            "src/Hosts/Api/Admins/OidcAuthentication.cs",
            "src/Hosts/Api/Admins/LoginService.cs",
            "src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs",
            "src/Persistence/Persistence.ControlPlane/Admins/ControlPlaneIdentityRecoveryReader.cs",
        };
        var source = string.Join('\n', files.Select(path => File.ReadAllText(Path.Combine(root, path))));

        Assert.Contains("TrySingleUuid(principal, \"tid\"", claims, StringComparison.Ordinal);
        Assert.Contains("TrySingleUuid(principal, \"oid\"", claims, StringComparison.Ordinal);
        Assert.Contains("GetByMicrosoftIdentityAsync", source, StringComparison.Ordinal);
        Assert.True(
            resolver.IndexOf("GetByMicrosoftIdentityAsync", StringComparison.Ordinal)
            < resolver.IndexOf("GetByEmployeeIdAsync", StringComparison.Ordinal),
            "EmployeeId may be evaluated as profile data only after exact Microsoft tuple resolution.");
        Assert.DoesNotContain("vcp.employee", source, StringComparison.Ordinal);
        Assert.DoesNotContain("preferred_username", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkforceEmailKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetByEmailAsync", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(ForbiddenRoleClaimRead(), source);
        Assert.DoesNotContain("JitProvisionMicrosoftAdminCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_create_wire_is_prebound_and_nullable_without_an_identity_mutation_route()
    {
        var root = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/Program.cs"));
        var handler = File.ReadAllText(Path.Combine(
            root, "src/Modules/Admins/Admins.Application/Users/CreateScopedAdmin.cs"));
        var routeStart = program.IndexOf("api.MapPost(\"/admins\"", StringComparison.Ordinal);
        var routeEnd = program.IndexOf("// --- Admin account management", routeStart, StringComparison.Ordinal);
        Assert.True(routeStart >= 0 && routeEnd > routeStart);
        var route = program[routeStart..routeEnd];

        Assert.Contains("body.ObjectId, body.Email, body.IdentityApprovalReference", route, StringComparison.Ordinal);
        Assert.Contains("RequireCsrf().RequireAuthorization(\"admin\").RequirePlatformUserTier(Tier.Super)",
            route, StringComparison.Ordinal);
        Assert.DoesNotContain("BindInvited", route, StringComparison.Ordinal);
        Assert.DoesNotContain("Email is required", route, StringComparison.Ordinal);
        Assert.Contains(
            "Guid ObjectId, string IdentityApprovalReference, string? Email = null", program,
            StringComparison.Ordinal);
        Assert.Contains("Guid AdminId, string? Email", program, StringComparison.Ordinal);
        Assert.Contains("GetRequiredTenantIdAsync", handler, StringComparison.Ordinal);
        Assert.Contains("GetByMicrosoftIdentityAsync", handler, StringComparison.Ordinal);
        Assert.Contains("command.ActingAdminId, approvalReference", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("GetByEmailAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("/{id:guid}/microsoft-identity", program, StringComparison.Ordinal);
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
    public void Production_compose_configures_microsoft_without_the_retired_google_provider()
    {
        var root = FindRepoRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.prod.yml"));

        Assert.Contains("AdminAuth__Providers__Microsoft__ClientId", compose, StringComparison.Ordinal);
        Assert.Contains("ADMIN_ENTRA_CLIENT_ID:?", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminAuth__Providers__Google", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("MerchantAuth__Providers__Google", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("admin_oidc_client_secret", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("merchant_user_oidc_client_secret", compose, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/Hosts/Api/Admins/LoginService.cs")]
    [InlineData("src/Hosts/Api/Admins/MicrosoftGraphEmployeeIdReader.cs")]
    [InlineData("src/Persistence/Persistence.ControlPlane/Admins/EmployeeProfileReader.cs")]
    public void Tier0_catch_paths_never_pass_exception_objects_to_logger(string path)
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, path));

        Assert.DoesNotMatch(LogExceptionObject(), source);
    }

    /// <summary>tier0-graph-employee-profile REQ-1.4-1.6, 9.1-9.6: the access token is never persisted by the
    /// framework and no Tier 0 log call names a token, employeeId, name, legacy key or Graph body.</summary>
    [Fact]
    public void Tier0_employee_profile_path_never_saves_tokens_or_logs_pii()
    {
        var root = FindRepoRoot();
        var oidc = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/Admins/OidcAuthentication.cs"));
        Assert.DoesNotMatch(SaveTokensEnabled(), oidc);

        var files = new[]
        {
            "src/Hosts/Api/Admins/OidcAuthentication.cs",
            "src/Hosts/Api/Admins/LoginService.cs",
            "src/Hosts/Api/Admins/MicrosoftGraphEmployeeIdReader.cs",
            "src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs",
            "src/Persistence/Persistence.ControlPlane/Admins/EmployeeProfileReader.cs",
        };
        foreach (var path in files)
            Assert.DoesNotMatch(LogPii(), File.ReadAllText(Path.Combine(root, path)));
    }

    [Fact]
    public void Employee_profile_runtime_has_only_the_exact_three_column_vibemp_path()
    {
        var root = FindRepoRoot();
        var reader = File.ReadAllText(Path.Combine(
            root, "src/Persistence/Persistence.ControlPlane/Admins/EmployeeProfileReader.cs"));
        var application = string.Join('\n', new[]
        {
            "src/Modules/Admins/Admins.Application/Users/EmployeeProfile.cs",
            "src/Modules/Admins/Admins.Application/Users/ResolveAdmin.cs",
            "src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs",
            "src/Hosts/Api/Admins/LoginService.cs",
        }.Select(path => File.ReadAllText(Path.Combine(root, path))));

        Assert.Contains("SELECT TOP (2) EmpCode, FirstNameTh, LastNameTh", reader, StringComparison.Ordinal);
        Assert.Contains("WHERE EmpCode = @employeeId", reader, StringComparison.Ordinal);
        Assert.Contains("new SqlParameter(\"@employeeId\"", reader, StringComparison.Ordinal);
        foreach (var writePrimitive in new[] { "INSERT ", "UPDATE ", "DELETE ", "ExecuteSql" })
            Assert.DoesNotContain(writePrimitive, reader, StringComparison.OrdinalIgnoreCase);

        var runtimeContext = File.ReadAllText(Path.Combine(
            root, "src/Persistence/Persistence.ControlPlane/ControlPlaneDbContext.cs"));
        Assert.DoesNotContain("DbSet<VibEmp", runtimeContext, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "dbo.branch", "DepartmentID", "und_brcode", "UndBrCode",
                     "FindOfficesAsync", "FindDivisionsAsync", "EmployeeProfileUnmapped",
                 })
        {
            Assert.DoesNotContain(forbidden, reader + application, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("employee-profile-sync", AuditAction.EmployeeProfileSync);
    }

    [Fact]
    public void Admin_graph_is_mandatory_only_at_new_oidc_callback_and_has_no_runtime_switch()
    {
        var root = FindRepoRoot();
        var oidc = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/Admins/OidcAuthentication.cs"));
        var options = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/OidcProviderOptions.cs"));
        var session = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/Admins/SessionAuthenticationHandler.cs"));
        var config = string.Join('\n', new[]
        {
            ".env.example",
            "docker-compose.prod.yml",
            "docs/runbooks/admin-microsoft-oidc.md",
        }.Select(path => File.ReadAllText(Path.Combine(root, path))));

        Assert.Contains("options.Scope.Add(\"User.Read\")", oidc, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(
            oidc, @"\breader\.ReadAsync\(", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Contains("context.ProtocolMessage.Error, \"consent_required\"", oidc, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtocolMessage.ErrorDescription", oidc, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception.Message", oidc, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireEmployeeProfile", options + oidc + config, StringComparison.Ordinal);
        Assert.DoesNotContain("MicrosoftGraphEmployeeIdReader", session, StringComparison.Ordinal);
        Assert.DoesNotContain("graph", session, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migrate_container_connection_is_built_in_process_without_adding_a_second_tool_contract()
    {
        const string configured = "Server=operator-provided";
        Assert.Equal(configured, WorkforceIdentityMigrationConnection.Resolve(
            configured, null, null, null, null, null));

        var generated = WorkforceIdentityMigrationConnection.Resolve(
            null, "db.example.internal", "14330", "VCentralPay", "synthetic;p=1", null);
        var parsed = new SqlConnectionStringBuilder(generated);
        Assert.Equal("db.example.internal,14330", parsed.DataSource);
        Assert.Equal("VCentralPay", parsed.InitialCatalog);
        Assert.Equal("sa", parsed.UserID);
        Assert.Equal("synthetic;p=1", parsed.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
        Assert.False(parsed.TrustServerCertificate);

        var strict = new SqlConnectionStringBuilder(WorkforceIdentityMigrationConnection.Resolve(
            null, "db.example.internal", null, "VCentralPay", "synthetic", "/run/secrets/db_ca_cert"));
        Assert.Equal(SqlConnectionEncryptOption.Strict, strict.Encrypt);
        Assert.Equal("/run/secrets/db_ca_cert", strict["Server Certificate"]);
        Assert.Equal("db.example.internal", strict["Host Name In Certificate"]);
        Assert.Null(WorkforceIdentityMigrationConnection.Resolve(
            null, null, null, "VCentralPay", "synthetic", null));
        var invalid = WorkforceIdentityMigrationConnection.Resolve(
            null, "db.example.internal", "70000", "VCentralPay", "synthetic", null);
        Assert.Null(invalid);
        var output = new StringWriter();
        var exitCode = await WorkforceIdentityMigration.RunAsync(
            invalid, new WorkforceIdentityMigrationInputs(null, null, null, null, null, null), output,
            CancellationToken.None);
        Assert.Equal(2, exitCode);
        Assert.Equal("[workforce-identity] failed: configuration" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Workforce_email_key_token_is_confined_to_immutable_migration_compatibility_artifacts()
    {
        var root = FindRepoRoot();
        var allowed = new[]
        {
            "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260823132337_Tier0WorkforceEmailIdentity.cs",
            "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260823132337_Tier0WorkforceEmailIdentity.Designer.cs",
            "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260830172117_Tier0EmployeeProfile.Designer.cs",
            "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260902133906_Tier0MicrosoftTenantAwareIdentity.cs",
        };
        var actual = ProductionSources(root)
            .Where(path => File.ReadAllText(path).Contains("WorkforceEmailKey", StringComparison.Ordinal))
            .Select(path => Relative(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowed.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Current_microsoft_runtime_has_no_retired_candidate_or_mutable_identity_api()
    {
        var root = FindRepoRoot();
        var directories = new[]
        {
            "src/Hosts/Api/Admins",
            "src/Modules/Admins/Admins.Application/Users",
            "src/Persistence/Persistence.ControlPlane/Admins",
        };
        var source = string.Join('\n', directories
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "src/Tools/WorkforceIdentityMigrator/Program.cs"))
            .Select(File.ReadAllText));
        var retired = new[]
        {
            "Tier0CandidatePolicy",
            "ListTier0CandidatesAsync",
            "WorkforceIdentityCandidate",
            "CanonicalEmail",
            "TrySelectIdentifier",
            "BindMicrosoftIdentity",
        };

        foreach (var symbol in retired)
            Assert.DoesNotContain(symbol, source, StringComparison.Ordinal);

        var exactPath = string.Join('\n', new[]
        {
            "src/Hosts/Api/Admins/MicrosoftWorkforceClaims.cs",
            "src/Hosts/Api/Admins/OidcAuthentication.cs",
            "src/Hosts/Api/Admins/LoginService.cs",
            "src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs",
            "src/Persistence/Persistence.ControlPlane/Admins/ControlPlaneIdentityRecoveryReader.cs",
        }.Select(path => File.ReadAllText(Path.Combine(root, path))));
        Assert.DoesNotContain("GetByEmailAsync", exactPath, StringComparison.Ordinal);
        Assert.DoesNotContain("BindSubject(", exactPath, StringComparison.Ordinal);
        Assert.DoesNotContain("preferred_username", exactPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_identity_tuple_writers_are_the_aggregate_factories_or_privileged_migration_artifacts()
    {
        foreach (var propertyName in new[] { nameof(User.Provider), nameof(User.TenantId), nameof(User.Subject) })
        {
            var setter = typeof(User).GetProperty(propertyName)!.SetMethod;
            Assert.NotNull(setter);
            Assert.True(setter!.IsPrivate, $"{propertyName} must remain aggregate-private.");
        }

        var root = FindRepoRoot();
        var offenders = ProductionSources(root)
            .Where(path => RawAdminTupleWrite().IsMatch(File.ReadAllText(path)))
            .Select(path => Relative(root, path))
            .Where(path => path != "src/Tools/WorkforceIdentityMigrator/Program.cs"
                && !path.StartsWith(
                    "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offenders);

        var aggregate = File.ReadAllText(Path.Combine(
            root, "src/Modules/Admins/Admins.Domain/Users/User.cs"));
        Assert.Contains("CreateScopedMicrosoft", aggregate, StringComparison.Ordinal);
        Assert.Contains("JitProvisionMicrosoft", aggregate, StringComparison.Ordinal);
        Assert.Contains("private static User NewMicrosoft", aggregate, StringComparison.Ordinal);
        Assert.Contains("Microsoft identities cannot use the historical bind path", aggregate, StringComparison.Ordinal);
    }

    [Fact]
    public void Workforce_logs_and_tool_output_exclude_identity_profile_token_and_manifest_values()
    {
        var root = FindRepoRoot();
        var paths = new[]
        {
            "src/Hosts/Api/Admins/OidcAuthentication.cs",
            "src/Hosts/Api/Admins/LoginService.cs",
            "src/Hosts/Api/Admins/MicrosoftGraphEmployeeIdReader.cs",
            "src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs",
            "src/Persistence/Persistence.ControlPlane/Admins/ControlPlaneIdentityRecoveryReader.cs",
            "src/Persistence/Persistence.ControlPlane/Admins/EmployeeProfileReader.cs",
            "src/Persistence/Persistence.ControlPlane/Admins/WorkforceTenantBindingStore.cs",
        };
        foreach (var path in paths)
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            foreach (Match call in LoggerCall().Matches(source))
                Assert.DoesNotMatch(SensitiveDiagnosticValue(), call.Value);
            Assert.DoesNotMatch(LogExceptionObject(), source);
        }

        var tool = File.ReadAllText(Path.Combine(root, "src/Tools/WorkforceIdentityMigrator/Program.cs"));
        var outputCalls = OutputCall().Matches(tool).Cast<Match>().Select(match => match.Value).ToArray();
        Assert.Equal(4, outputCalls.Length);
        Assert.All(outputCalls, call => Assert.DoesNotMatch(SensitiveToolOutputValue(), call));
        Assert.Contains(outputCalls, call => call.Contains(
            "SnapshotCount} mapped={verified.MappedCount} no-op={verified.NoOpCount}", StringComparison.Ordinal));
        Assert.Contains(outputCalls, call => call.Contains(
            "SnapshotCount} mapped={completed.MappedCount} no-op={completed.NoOpCount}", StringComparison.Ordinal));
        Assert.DoesNotContain("Exception.Message", tool, StringComparison.Ordinal);
    }

    [Fact]
    public void Tier0_tool_keeps_the_existing_dependency_floor()
    {
        var root = FindRepoRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src/Tools/WorkforceIdentityMigrator/WorkforceIdentityMigrator.csproj"));
        var packages = PackageReference().Matches(project).Cast<Match>()
            .Select(match => match.Groups["id"].Value)
            .ToArray();

        Assert.Equal(["Microsoft.Data.SqlClient"], packages);
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

    [Fact]
    public void Admin_identity_documentation_describes_only_the_final_tenant_aware_contract()
    {
        var root = FindRepoRoot();
        var rollout = File.ReadAllText(Path.Combine(root, "docs/runbooks/admin-workforce-jit-rollout.md"));
        var oidc = File.ReadAllText(Path.Combine(root, "docs/runbooks/admin-microsoft-oidc.md"));
        var deploy = File.ReadAllText(Path.Combine(root, "docs/runbooks/deploy-self-host.md"));
        var local = File.ReadAllText(Path.Combine(root, "docs/runbooks/local-dev-run.md"));
        var reference = File.ReadAllText(Path.Combine(root, "docs/reference/admins.md"));
        var appsettings = File.ReadAllText(Path.Combine(root, "src/Hosts/Api/appsettings.json"));
        var documentation = string.Join('\n', rollout, oidc, deploy, local, reference, appsettings);

        Assert.Contains("\"schemaVersion\": 1", rollout, StringComparison.Ordinal);
        Assert.Contains("WORKFORCE_IDENTITY_MANIFEST_SHA256", rollout, StringComparison.Ordinal);
        Assert.Contains("WORKFORCE_IDENTITY_TARGET", rollout, StringComparison.Ordinal);
        Assert.Contains("SnapshotCount, MappedCount, NoOpCount", rollout, StringComparison.Ordinal);
        Assert.Contains("approved tenant-registry design", rollout, StringComparison.Ordinal);
        Assert.Contains("Email เป็น optional non-unique contact", oidc, StringComparison.Ordinal);
        Assert.Contains("ไม่มี supported endpoint", oidc, StringComparison.Ordinal);
        Assert.Contains("23 migrations", deploy, StringComparison.Ordinal);
        Assert.Contains("20260902133906_Tier0MicrosoftTenantAwareIdentity", local, StringComparison.Ordinal);
        Assert.Contains("identityApprovalReference", reference, StringComparison.Ordinal);
        Assert.Contains("immutable tenant-aware tuple", appsettings, StringComparison.Ordinal);

        var staleGuidance = new[]
        {
            "canonical corporate email",
            "canonical-email",
            "canonical `viriyah.co.th` email",
            "ไม่ใช้ `oid`",
            "does not read `oid`",
            "เลือก `email` ก่อน",
            "email มาก่อน",
            "matching canonical email",
            "WorkforceEmailKey ตรง canonicalizer",
            "ต้องอยู่ exact domain",
        };
        foreach (var phrase in staleGuidance)
            Assert.DoesNotContain(phrase, documentation, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"SaveTokens\s*=\s*true", RegexOptions.CultureInvariant)]
    private static partial Regex SaveTokensEnabled();

    [GeneratedRegex(@"Log(?:Error|Warning|Information|Debug|Trace|Critical)\s*\([^;]*\b(?:accessToken|employeeId|EmployeeId|FirstName|LastName|LegacyKey|legacyKey|branchCode|departmentId|raw|body)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LogPii();

    /// <summary><c>User.ApplyEmployeeProfile</c> is the ONLY writer of mutable HR attribute
    /// <c>EmployeeId</c> — no endpoint or unrelated command may assign it.</summary>
    [Fact]
    public void EmployeeId_is_assigned_only_inside_the_user_aggregate()
    {
        var root = FindRepoRoot();
        var offenders = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(Path.Combine("Admins.Domain", "Users", "User.cs"), StringComparison.Ordinal))
            .Where(path => !path.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(path => EmployeeIdAssignment().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\bEmployeeId\s*=(?!=)[^;{}]*;", RegexOptions.CultureInvariant)]
    private static partial Regex EmployeeIdAssignment();

    [GeneratedRegex("Find(?:All|First)\\s*\\(\\s*\"roles\"", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenRoleClaimRead();

    [GeneratedRegex("Log(?:Error|Warning|Information)\\s*\\(\\s*(?:ex|exception)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex LogExceptionObject();

    [GeneratedRegex(
        @"\b(?:UPDATE|INSERT\s+(?:INTO\s+)?)\s+(?:\[?admin\]?\.)?\[?Users\]?\b.{0,1200}?\b(?:TenantId|Subject)\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex RawAdminTupleWrite();

    [GeneratedRegex(
        @"\b(?:logger|_logger)\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\([\s\S]*?\);",
        RegexOptions.CultureInvariant)]
    private static partial Regex LoggerCall();

    [GeneratedRegex(
        @"\b(?:TenantId|ObjectId|Subject|Email|EmployeeId|authorizationCode|accessToken|idToken|sessionToken|cookie|manifest|digest|evidence|target|responseBody|raw|body)\b|[""'](?:tid|oid)[""']|Exception\.Message",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveDiagnosticValue();

    [GeneratedRegex(@"output\.WriteLineAsync\s*\([\s\S]*?\);", RegexOptions.CultureInvariant)]
    private static partial Regex OutputCall();

    [GeneratedRegex(
        @"\b(?:ManifestFile|ManifestSha256|ApprovedTarget|ApprovalEvidence|TenantId|ObjectId|Email|EmployeeId|token|cookie|contents|path|digest|evidence|target|raw|body|response|exception)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveToolOutputValue();

    [GeneratedRegex(@"<PackageReference\s+Include=""(?<id>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex PackageReference();

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
