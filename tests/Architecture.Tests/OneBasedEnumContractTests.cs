using BuildingBlocks.Application;
using Admins.Domain.Users;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Orders.Domain;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Architecture.Tests;

public sealed class OneBasedEnumContractTests
{
    [Fact]
    public void Control_plane_and_identity_enums_use_current_values()
    {
        Assert.Equal(1, (int)SessionLookupStatus.Active);
        Assert.Equal(2, (int)SessionLookupStatus.Superseded);
        Assert.Equal(3, (int)SessionLookupStatus.Revoked);
        Assert.Equal(1, (int)Admins.Domain.Users.SessionStatus.Active);
        Assert.Equal(2, (int)Admins.Domain.Users.SessionStatus.Superseded);
        Assert.Equal(3, (int)Admins.Domain.Users.SessionStatus.Revoked);
        Assert.Equal(1, (int)Tier.Scoped);
        Assert.Equal(2, (int)Tier.Super);
        Assert.Equal(1, (int)Admins.Domain.Users.UserStatus.Active);
        Assert.Equal(2, (int)Admins.Domain.Users.UserStatus.Suspended);

        Assert.Equal(1, (int)Scope.Platform);
        Assert.Equal(2, (int)Scope.Merchant);
        Assert.Equal(1, (int)PermissionStatus.Active);
        Assert.Equal(2, (int)PermissionStatus.Inactive);
        Assert.Equal(1, (int)RoleStatus.Active);
        Assert.Equal(2, (int)RoleStatus.Inactive);
    }

    [Fact]
    public void Merchant_commerce_and_payment_enums_use_current_values()
    {
        Assert.Equal(1, (int)MerchantStatus.Active);
        Assert.Equal(2, (int)MerchantStatus.Inactive);
        Assert.Equal(1, (int)IdentityType.Individual);
        Assert.Equal(2, (int)IdentityType.Juristic);
        Assert.Equal(1, (int)Merchants.Domain.Users.SessionStatus.Active);
        Assert.Equal(2, (int)Merchants.Domain.Users.SessionStatus.Superseded);
        Assert.Equal(3, (int)Merchants.Domain.Users.SessionStatus.Revoked);
        Assert.Equal(1, (int)TicketPurpose.Registration);
        Assert.Equal(2, (int)TicketPurpose.Correction);
        Assert.Equal(1, (int)Merchants.Domain.Users.UserStatus.PendingApproval);
        Assert.Equal(2, (int)Merchants.Domain.Users.UserStatus.Active);
        Assert.Equal(3, (int)Merchants.Domain.Users.UserStatus.Rejected);
        Assert.Equal(4, (int)Merchants.Domain.Users.UserStatus.Suspended);

        Assert.Equal(1, (int)OrderStatus.Pending);
        Assert.Equal(2, (int)OrderStatus.Paid);
        Assert.Equal(3, (int)OrderStatus.Failed);
        Assert.Equal(4, (int)OrderStatus.Expired);
        Assert.Equal(5, (int)OrderStatus.Refunded);
        Assert.Equal(6, (int)OrderStatus.Cancelled);
        Assert.Equal(1, (int)Payments.Domain.SessionStatus.Created);
        Assert.Equal(2, (int)Payments.Domain.SessionStatus.Redirected);
        Assert.Equal(3, (int)Payments.Domain.SessionStatus.Paid);
        Assert.Equal(4, (int)Payments.Domain.SessionStatus.Failed);
        Assert.Equal(5, (int)Payments.Domain.SessionStatus.Expired);
        Assert.Equal(1, (int)Code.TwoCTwoP);
        Assert.Equal(2, (int)Code.Omise);
    }

    [Fact]
    public void Non_target_cart_status_remains_zero_based()
    {
        Assert.Equal(0, (int)Carts.Domain.CartStatus.Open);
    }
}
