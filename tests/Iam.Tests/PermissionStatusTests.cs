using Iam.Domain.Permissions;

namespace Iam.Tests;

public sealed class PermissionStatusTests
{
    [Fact]
    public void Catalog_entries_default_active_and_can_be_deactivated()
    {
        var group = new PermissionGroup("orders", Scope.Merchant, "Orders", 1);
        var permission = new Permission("orders.read", "orders", "Read orders", 1);

        Assert.Equal(PermissionStatus.Active, group.Status);
        Assert.Equal(PermissionStatus.Active, permission.Status);

        group.Deactivate();
        permission.Deactivate();

        Assert.Equal(PermissionStatus.Inactive, group.Status);
        Assert.Equal(PermissionStatus.Inactive, permission.Status);
    }
}
