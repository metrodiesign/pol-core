using System.Diagnostics;

namespace Architecture.Tests;

/// <summary>
/// Proves the REQ-1.7 project guard fails a separate build. The fixture deliberately makes
/// <c>Pol.Domain</c> reference <c>Pol.Infrastructure</c>; an in-process assertion alone cannot prove
/// that MSBuild rejects this mutation.
/// </summary>
public sealed class CompileNegativeReferenceTests
{
    public static TheoryData<string, string, string> ForbiddenLayerReferences()
    {
        var data = new TheoryData<string, string, string>();
        data.Add("Bad.csproj", "Domain", "Pol.Infrastructure");
        data.Add("DomainToApi.csproj", "Domain", "Pol.Api");
        data.Add("ApplicationToInfrastructure.csproj", "Application", "Pol.Infrastructure");
        data.Add("ApplicationToApi.csproj", "Application", "Pol.Api");
        return data;
    }

    [Theory]
    [MemberData(nameof(ForbiddenLayerReferences))]
    public async Task Forbidden_ProjectReference_from_inward_layer_fails_the_build(
        string projectFile, string layer, string target)
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = Path.Combine(repoRoot, "tests", "Pol.ArchitectureTests", "Fixtures",
            "ForbiddenLayerReference", projectFile);
        Assert.True(File.Exists(fixturePath), $"Fixture project not found at {fixturePath}");

        var psi = new ProcessStartInfo("dotnet", $"build \"{fixturePath}\" --nologo --disable-build-servers")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(process.ExitCode != 0,
            $"Expected the fixture build to FAIL (forbidden ProjectReference), but it exited 0. stdout: {stdout}");
        Assert.Contains("Forbidden ProjectReference", stdout + stderr);
        Assert.Contains($"platform layer '{layer}'", stdout + stderr);
        Assert.Contains(target, stdout + stderr);
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
