namespace Architecture.Tests;

public sealed class OneBasedReferenceParityTests
{
    [Fact]
    public void Entity_reference_lists_current_one_based_contract_and_migration_safety()
    {
        var root = FindRepoRoot();
        var reference = File.ReadAllText(Path.Combine(root, "docs/reference/entity-fields.md"));

        foreach (var field in Fields)
            Assert.Contains($"| `{field.Column}` | `int` | NN | `{field.Mapping}` |", Section(reference, field.Table),
                StringComparison.Ordinal);

        Assert.Contains("20260808161508_OneBasedPersistedEnumStorage", reference, StringComparison.Ordinal);
        Assert.Contains("merch.Users.IdentityType` หรือ `merch.RegistrationAttempts.IdentityType` เป็น `NULL`", reference,
            StringComparison.Ordinal);
        Assert.Contains("ไม่ backfill", reference, StringComparison.Ordinal);
        Assert.Contains("Status IN (1, 2)", reference, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string Section(string reference, string table)
    {
        var start = reference.IndexOf($"### `{table}`", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Reference section missing: {table}");
        var end = reference.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        return reference[start..(end < 0 ? reference.Length : end)];
    }

    private static readonly (string Table, string Column, string Mapping)[] Fields =
    [
        ("admin.Sessions", "Status", "Active=1`, `Superseded=2`, `Revoked=3"),
        ("admin.Users", "Tier", "Scoped=1`, `Super=2"),
        ("admin.Users", "Status", "Active=1`, `Suspended=2"),
        ("iam.PermissionGroups", "Scope", "Platform=1`, `Merchant=2"),
        ("iam.PermissionGroups", "Status", "Active=1`, `Inactive=2"),
        ("iam.Permissions", "Status", "Active=1`, `Inactive=2"),
        ("iam.Roles", "Status", "Active=1`, `Inactive=2"),
        ("iam.Roles", "Scope", "Platform=1`, `Merchant=2"),
        ("merch.Merchants", "Status", "Active=1`, `Inactive=2"),
        ("merch.RegistrationAttempts", "Purpose", "Registration=1`, `Correction=2"),
        ("merch.RegistrationAttempts", "IdentityType", "Individual=1`, `Juristic=2"),
        ("merch.Sessions", "Status", "Active=1`, `Superseded=2`, `Revoked=3"),
        ("merch.Users", "Status", "PendingApproval=1`, `Active=2`, `Rejected=3`, `Suspended=4"),
        ("merch.Users", "IdentityType", "Individual=1`, `Juristic=2"),
        ("shop.Orders", "Status", "Pending=1`, `Paid=2`, `Failed=3`, `Expired=4`, `Refunded=5`, `Cancelled=6"),
        ("txn.PaymentSessions", "Psp", "TwoCTwoP=1`, `Omise=2"),
        ("txn.PaymentSessions", "Status", "Created=1`, `Redirected=2`, `Paid=3`, `Failed=4`, `Expired=5"),
        ("txn.PspConnections", "Psp", "TwoCTwoP=1`, `Omise=2"),
    ];
}
