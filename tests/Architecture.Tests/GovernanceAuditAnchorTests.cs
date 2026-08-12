using Governance.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Persistence.ControlPlane.Governance;

namespace Architecture.Tests;

public sealed class GovernanceAuditAnchorTests
{
    [Fact]
    public async Task File_anchor_is_idempotent_append_only_and_detects_tampering()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pol-audit-anchor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var keyFile = Path.Combine(root, "signing-key");
            var anchorFile = Path.Combine(root, "anchors.jsonl");
            await File.WriteAllTextAsync(keyFile, Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()));
            using var store = new FileAuditAnchorStore(new AuditAnchorOptions(anchorFile, keyFile));
            var now = new DateTime(2026, 8, 10, 5, 0, 0, DateTimeKind.Utc);
            var platform = new AuditAnchorCheckpoint("platform", 1, new string('a', 64), now);
            await store.AppendAsync(platform, default);
            await store.AppendAsync(platform, default);
            await store.AppendAsync(new AuditAnchorCheckpoint(
                $"merchant:{Guid.NewGuid():D}", 1, new string('b', 64), now), default);

            Assert.Equal(2, File.ReadAllLines(anchorFile).Length);
            Assert.Equal(2, (await store.ReadAllLatestAsync(default)).Count);

            var contents = await File.ReadAllTextAsync(anchorFile);
            await File.WriteAllTextAsync(anchorFile, contents.Replace(
                new string('a', 64), new string('c', 64), StringComparison.Ordinal));
            await Assert.ThrowsAsync<AuditIntegrityException>(() => store.ReadAllLatestAsync(default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void File_anchor_rejects_placeholder_signing_key()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pol-audit-anchor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var keyFile = Path.Combine(root, "signing-key");
            File.WriteAllText(keyFile, Convert.ToBase64String(new byte[32]));
            Assert.Throws<InvalidOperationException>(() =>
                new FileAuditAnchorStore(new AuditAnchorOptions(Path.Combine(root, "anchors.jsonl"), keyFile)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Production_registration_requires_both_external_paths()
    {
        var empty = new ConfigurationBuilder().Build();
        using (var provider = BuildProvider(empty, new TestEnvironment("Production")))
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IAuditAnchorStore>());

        var partial = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuditAnchor:Path"] = "/var/lib/pol-core/audit/anchors.jsonl",
        }).Build();
        using var partialProvider = BuildProvider(partial, new TestEnvironment("Development"));
        Assert.Throws<InvalidOperationException>(() => partialProvider.GetRequiredService<IAuditAnchorStore>());
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration, IHostEnvironment environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton(environment);
        services.AddGovernanceAuditAnchoring();
        return services.BuildServiceProvider();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
