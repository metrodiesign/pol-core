using System.Diagnostics;

namespace Architecture.Tests;

/// <summary>
/// Proves the Directory.Build.props guard (RlsToQueryFilter_EnforcePersistenceBoundaries, rls-to-query-filter
/// REQ-11.8) actually fails a BUILD, not just an arch-lint assertion — an in-process xUnit assertion cannot
/// itself prove a SEPARATE `dotnet build` invocation fails, so this shells a real `dotnet build` against a
/// fixture project (<c>Fixtures/ForbiddenControlPlaneReference/Bad.csproj</c>) that deliberately references
/// <c>Persistence.ControlPlane</c> from a project not on the allowlist, and asserts the process exits
/// non-zero with the expected error.
/// </summary>
public sealed class CompileNegativeReferenceTests
{
    [Fact]
    public void Forbidden_ProjectReference_to_Persistence_ControlPlane_fails_the_build()
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = Path.Combine(repoRoot, "tests", "Architecture.Tests", "Fixtures",
            "ForbiddenControlPlaneReference", "Bad.csproj");
        Assert.True(File.Exists(fixturePath), $"Fixture project not found at {fixturePath}");

        var psi = new ProcessStartInfo("dotnet", $"build \"{fixturePath}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode != 0,
            $"Expected the fixture build to FAIL (forbidden ProjectReference), but it exited 0. stdout: {stdout}");
        Assert.Contains("Forbidden ProjectReference", stdout + stderr);
        Assert.Contains("Persistence.ControlPlane", stdout + stderr);
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
