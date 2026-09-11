namespace Architecture.Tests;

public sealed class LegacyRetirementProofTests
{
    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Retired_context_factories_and_runtime_constructors_have_no_source_consumer()
    {
        var root = FindRepoRoot();
        Assert.False(File.Exists(Path.Combine(root,
            "src/Pol.Infrastructure/Persistence/Persistence.MerchantUsers/MerchantUserDbContextFactory.cs")));
        Assert.False(File.Exists(Path.Combine(root,
            "src/Pol.Infrastructure/Persistence/Persistence.MerchantRuntime/MerchantRuntimeDbContextFactory.cs")));

        var source = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(path => (Path: Path.GetRelativePath(root, path), Text: File.ReadAllText(path)))
            .ToArray();

        var runtimeConstructors = source
            .Where(file => file.Text.Contains("new MerchantUserDbContext", StringComparison.Ordinal)
                || file.Text.Contains("new MerchantRuntimeDbContext", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();

        Assert.Empty(runtimeConstructors);
        Assert.Contains(source, file => file.Text.Contains("new CommerceDbContext", StringComparison.Ordinal));
        Assert.Contains(source, file => file.Text.Contains("new ControlPlaneDbContext", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
