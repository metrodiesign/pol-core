namespace Architecture.Tests;

public sealed class OneBasedMigrationShapeTests
{
    [Fact]
    public void Forward_migration_preflights_before_mutation_and_maps_every_target_field()
    {
        var migration = ReadMigration();
        var down = migration.IndexOf("protected override void Down", StringComparison.Ordinal);
        Assert.True(down > 0);
        var up = migration[..down];

        Assert.True(up.IndexOf("merch.Users WHERE [IdentityType] IS NULL", StringComparison.Ordinal)
            < up.IndexOf("migrationBuilder.DropCheckConstraint", StringComparison.Ordinal));
        Assert.True(up.IndexOf("merch.RegistrationAttempts WHERE [IdentityType] IS NULL", StringComparison.Ordinal)
            < up.IndexOf("migrationBuilder.DropCheckConstraint", StringComparison.Ordinal));
        Assert.DoesNotContain("defaultValue:", up, StringComparison.Ordinal);

        foreach (var target in Targets)
            Assert.Contains($"UPDATE {target.Table} SET [{target.Column}]", up, StringComparison.Ordinal);

        Assert.Contains("nullable: false", up, StringComparison.Ordinal);
        Assert.Contains("sql: \"([Scope] = 1 AND [MerchantId] IS NULL) OR [Scope] = 2\"", up,
            StringComparison.Ordinal);
        Assert.Contains("filter: \"[Status] IN (1, 2)\"", up, StringComparison.Ordinal);
    }

    [Fact]
    public void Reverse_migration_has_preflight_and_restores_legacy_contract()
    {
        var migration = ReadMigration();
        var down = migration.IndexOf("protected override void Down", StringComparison.Ordinal);
        var body = migration[down..];

        Assert.Contains("merch.Users WHERE [IdentityType] IS NULL", body, StringComparison.Ordinal);
        Assert.Contains("merch.RegistrationAttempts WHERE [IdentityType] IS NULL", body, StringComparison.Ordinal);
        Assert.Contains("nullable: true", body, StringComparison.Ordinal);
        Assert.Contains("sql: \"([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1\"", body,
            StringComparison.Ordinal);
        Assert.Contains("filter: \"[Status] IN (0, 1)\"", body, StringComparison.Ordinal);

        foreach (var target in Targets)
            Assert.Contains($"UPDATE {target.Table} SET [{target.Column}]", body, StringComparison.Ordinal);
    }

    private static string ReadMigration()
    {
        var root = FindRepoRoot();
        var files = Directory.EnumerateFiles(
                Path.Combine(root, "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations"),
                "*_OneBasedPersistedEnumStorage.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(files);
        return File.ReadAllText(files[0]);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static readonly (string Table, string Column)[] Targets =
    [
        ("admin.Sessions", "Status"),
        ("admin.Users", "Tier"),
        ("admin.Users", "Status"),
        ("iam.PermissionGroups", "Scope"),
        ("iam.PermissionGroups", "Status"),
        ("iam.Permissions", "Status"),
        ("iam.Roles", "Status"),
        ("iam.Roles", "Scope"),
        ("cfg.Positions", "Status"),
        ("cfg.Offices", "Status"),
        ("cfg.Levels", "Status"),
        ("cfg.Divisions", "Status"),
        ("merch.Merchants", "Status"),
        ("merch.RegistrationAttempts", "Purpose"),
        ("merch.RegistrationAttempts", "IdentityType"),
        ("merch.Sessions", "Status"),
        ("merch.Users", "Status"),
        ("merch.Users", "IdentityType"),
        ("shop.Orders", "Status"),
        ("txn.PaymentSessions", "Psp"),
        ("txn.PaymentSessions", "Status"),
        ("txn.PspConnections", "Psp"),
    ];
}
