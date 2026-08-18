namespace Architecture.Tests;

/// <summary>
/// host-test-config-precedence REQ-2 — config a test sets for a booted host must be the config the host uses.
/// <c>Program.cs</c> reads <c>GetConnectionString("App")</c> BEFORE <c>builder.Build()</c>, and a value injected
/// through a factory's <c>ConfigureAppConfiguration</c> in-memory source is applied too late for that read (and
/// sits BELOW machine-local appsettings/user-secrets), so the host silently falls back to whatever the machine
/// has — green on dev, "Login failed for user 'pol_app'" on CI (PR #184). Keys the host reads at build time must
/// go through <c>UseSetting</c>, which is host config: first to arrive, highest precedence.
/// <para>This is a plain text scan (balanced-paren region after each invocation), not Roslyn: it must catch the
/// key inside interpolated strings too. Known accepted loophole: a key assembled from several string pieces is
/// invisible to it — the precedence canary (<c>HostConfigPrecedenceCanaryTests</c>) and review are the next
/// layers for that.</para>
/// </summary>
public sealed class HostTestConfigGateTests
{
    /// <summary>Key prefixes Program.cs reads before <c>builder.Build()</c> today. Add a member only with the
    /// build-time read site as evidence (Fact 2 pins the current one so this list cannot rot).</summary>
    private static readonly string[] BuildTimeKeyPrefixes = ["ConnectionStrings"];

    /// <summary>The one file allowed to put a banned prefix inside <c>ConfigureAppConfiguration</c>: the
    /// precedence canary, whose whole job is planting a conflicting value in the losing channel to prove the
    /// winning one reached the host. Kept honest by the staleness fact below.</summary>
    private const string CanaryExemption = "tests/Hosts.Tests/HostConfigPrecedenceCanaryTests.cs";

    // Composed at runtime so this gate's own source never contains the token it scans for.
    private static readonly string ConfigureAppConfigurationToken = ".ConfigureAppConfiguration" + "(";

    /// <summary>Pre-Build <c>builder.Configuration</c> uses in Program.cs that are proven safe (lazy
    /// <c>.GetSection(</c> options binding is allowed generically) or known-and-pinned build-time reads.
    /// A new entry needs proof that tests can inject its keys via <c>UseSetting</c>.</summary>
    private static readonly string[] AllowedPreBuildConfigurationUses =
    [
        "AddSecurityTelemetry(builder.Configuration",              // Seq:IngestionUrl — eager, test-benign
        "builder.Configuration.GetConnectionString(\"App\")",      // the pinned trap (Fact 2)
        "ProvisioningGuards.RequireOidcProviders(builder.Configuration", // non-Development only
        "ProvisioningGuards.RequirePublicBaseUrl(builder.Configuration)", // non-Development only
        "AddMerchantUserOidcAuthentication(builder.Configuration", // eager; tests set MerchantAuth:* via UseSetting
        "AddAdminOidcAuthentication(builder.Configuration",        // eager; tests set AdminAuth:* via UseSetting
        "AddConsoleConfiguration(builder.Configuration",           // lazy capture; resolves after Build (provider-stack test)
    ];

    private const string ProgramCs = "src/Hosts/Api/Program.cs";
    private const string BuildLine = "var app = builder.Build();";

    [Fact] // Fact 1 — the ban (REQ-2.1, 2.3, 2.4)
    public void No_test_sets_a_build_time_key_through_ConfigureAppConfiguration()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                continue;
            if (relative == CanaryExemption)
                continue;

            var text = File.ReadAllText(file);
            for (var at = text.IndexOf(ConfigureAppConfigurationToken, StringComparison.Ordinal);
                 at >= 0;
                 at = text.IndexOf(ConfigureAppConfigurationToken, at + 1, StringComparison.Ordinal))
            {
                var region = BalancedRegion(text, at + ConfigureAppConfigurationToken.Length - 1);
                foreach (var prefix in BuildTimeKeyPrefixes)
                    if (region.Contains(prefix, StringComparison.Ordinal))
                        offenders.Add($"{relative}: sets a '{prefix}' key inside ConfigureAppConfiguration");
            }
        }

        Assert.True(offenders.Count == 0,
            "Program.cs reads these keys BEFORE builder.Build(); a ConfigureAppConfiguration in-memory source "
            + "arrives too late and loses to machine-local appsettings, so the host silently boots with another "
            + "machine's value (green on dev, login-failure on CI — PR #184). Set them as host config instead: "
            + "builder.UseSetting(\"ConnectionStrings:App\", ...). Offenders:\n"
            + string.Join("\n", offenders));
    }

    [Fact] // Fact 2 — pin the build-time read (REQ-2.6: the ban may not outlive its reason)
    public void Program_cs_still_reads_the_App_connection_string_before_Build()
    {
        Assert.True(PreBuildText(FindRepoRoot()).Contains("GetConnectionString(\"App\")", StringComparison.Ordinal),
            $"{ProgramCs} no longer reads GetConnectionString(\"App\") before builder.Build() — the reason for "
            + "banning ConnectionStrings in test ConfigureAppConfiguration blocks is gone. Remove the prefix from "
            + "BuildTimeKeyPrefixes (and this fact) instead of leaving a rotten ban in place.");
    }

    [Fact] // Fact 3 — no new member of the failure class (REQ-2.4, edge case: Vault turned eager again)
    public void Every_pre_Build_configuration_use_in_Program_cs_is_allowlisted()
    {
        var offenders = PreBuildText(FindRepoRoot())
            .Split('\n')
            .Select((line, index) => (line, number: index + 1))
            .Where(l => l.line.Contains("builder.Configuration", StringComparison.Ordinal))
            .Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => !l.line.Contains("builder.Configuration.GetSection(", StringComparison.Ordinal))
            .Where(l => !AllowedPreBuildConfigurationUses.Any(allowed => l.line.Contains(allowed, StringComparison.Ordinal)))
            .Select(l => $"{ProgramCs}:{l.number}: {l.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "New builder.Configuration read before builder.Build() in Program.cs. Every key read there is "
            + "invisible to test ConfigureAppConfiguration sources (the PR #184 failure class). Either read it "
            + "after Build (lazy options/DI), or add it to AllowedPreBuildConfigurationUses WITH proof that tests "
            + "inject its keys via UseSetting. Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact] // Fact 4 — allowlist + exemption may not rot (REQ-2.6)
    public void Every_allowlist_entry_and_the_canary_exemption_are_still_live()
    {
        var repoRoot = FindRepoRoot();
        var preBuild = PreBuildText(repoRoot);
        var stale = AllowedPreBuildConfigurationUses
            .Where(entry => !preBuild.Contains(entry, StringComparison.Ordinal))
            .Select(entry => $"allowlist entry no longer in {ProgramCs} before Build: {entry}")
            .ToList();

        var canaryPath = Path.Combine(repoRoot, CanaryExemption);
        if (!File.Exists(canaryPath) || !BuildTimeKeyPrefixes.Any(p =>
                File.ReadAllText(canaryPath).Contains(p, StringComparison.Ordinal)))
            stale.Add($"canary exemption is stale: {CanaryExemption} is gone or no longer plants a banned key");

        Assert.True(stale.Count == 0,
            "Drop the rotten entries — a stale allowlist quietly widens the gate's blind spot:\n"
            + string.Join("\n", stale));
    }

    /// <summary>The invocation's argument region: from the opening paren until parens balance. Naive about
    /// parens inside strings/comments; if the region never balances the rest of the file is scanned, which can
    /// only over-report, never under-report.</summary>
    private static string BalancedRegion(string text, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0)
                return text[openParen..(i + 1)];
        }
        return text[openParen..];
    }

    private static string PreBuildText(string repoRoot)
    {
        var text = File.ReadAllText(Path.Combine(repoRoot, ProgramCs));
        var buildAt = text.IndexOf(BuildLine, StringComparison.Ordinal);
        Assert.True(buildAt >= 0,
            $"{ProgramCs} no longer contains '{BuildLine}' — update HostTestConfigGateTests to the new build call.");
        return text[..buildAt];
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
