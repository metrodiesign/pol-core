using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>Task10 guard for REQ-1.6. Every live EF configuration in the source tree keeps one physical
/// schema-table mapping owner. Historical migration Designer snapshots are excluded because they are immutable
/// history; all current module and persistence configurations are scanned.</summary>
public sealed class RuntimeMappingOwnerTests
{
    private static readonly string[] RuntimeRoots = ["src/Infrastructure"];

    private static readonly Regex ConfigurationStart = new(
        @"(?:class|record)\s+(?<name>\w+)[^\r\n{]*IEntityTypeConfiguration<(?<entity>[^>]+)>",
        RegexOptions.Compiled);

    private static readonly Regex TableMapping = new(
        @"ToTable\(""(?<table>[^""]+)""(?:,\s*SchemaNames\.(?<schema>\w+))?",
        RegexOptions.Compiled);

    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Runtime_physical_tables_have_one_configuration_owner()
    {
        var root = FindRepoRoot();
        var mappings = new List<RuntimeMapping>();

        foreach (var relativeRoot in RuntimeRoots)
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, relativeRoot), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "Migrations" + Path.DirectorySeparatorChar)
                         && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var source = File.ReadAllText(path);
            var starts = ConfigurationStart.Matches(source);
            for (var index = 0; index < starts.Count; index++)
            {
                var start = starts[index];
                var end = index + 1 < starts.Count ? starts[index + 1].Index : source.Length;
                var block = source[start.Index..end];
                var table = TableMapping.Match(block);
                if (!table.Success)
                    continue;

                mappings.Add(new RuntimeMapping(
                    table.Groups["schema"].Value is { Length: > 0 } schema ? schema : "<default>",
                    table.Groups["table"].Value,
                    start.Groups["entity"].Value.Trim(),
                    Path.GetRelativePath(root, path),
                    start.Groups["name"].Value));
            }
        }

        Assert.NotEmpty(mappings);
        var duplicates = mappings
            .GroupBy(mapping => (mapping.Schema, mapping.Table))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Schema}.{group.Key.Table}: "
                + string.Join(", ", group.Select(item => $"{item.Entity} @ {item.Path}::{item.Configuration}")))
            .ToArray();

        Assert.True(duplicates.Length == 0,
            "Multiple runtime configuration owners map the same physical table: " + string.Join(" | ", duplicates));
    }

    private sealed record RuntimeMapping(string Schema, string Table, string Entity, string Path, string Configuration);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
