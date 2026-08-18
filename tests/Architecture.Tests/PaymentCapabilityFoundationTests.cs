using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Payments.Domain.Capabilities;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUsers;

namespace Architecture.Tests;

public sealed class PaymentCapabilitySchemaTests
{
    [Fact]
    public void Capability_entities_use_expected_schema_and_Guid_identity()
    {
        using var db = PaymentCapabilityModel.Pol();
        var expected = new Dictionary<Type, (string Schema, string Table)>
        {
            [typeof(PaymentMethod)] = (SchemaNames.Cfg, "PaymentMethods"),
            [typeof(PaymentMethodOptionGroup)] = (SchemaNames.Cfg, "PaymentMethodOptionGroups"),
            [typeof(PaymentMethodOption)] = (SchemaNames.Cfg, "PaymentMethodOptions"),
            [typeof(PaymentProvider)] = (SchemaNames.Cfg, "PaymentProviders"),
            [typeof(PaymentProviderMethod)] = (SchemaNames.Cfg, "PaymentProviderMethods"),
            [typeof(PaymentProviderMethodOption)] = (SchemaNames.Cfg, "PaymentProviderMethodOptions"),
            [typeof(MerchantProviderAccountMethod)] = (SchemaNames.Txn, "MerchantProviderAccountMethods"),
            [typeof(MerchantProviderAccountMethodOption)] = (SchemaNames.Txn, "MerchantProviderAccountMethodOptions"),
            [typeof(MerchantPaymentMethod)] = (SchemaNames.Txn, "MerchantPaymentMethods"),
            [typeof(MerchantUserPaymentMethod)] = (SchemaNames.Txn, "MerchantUserPaymentMethods"),
        };

        foreach (var (type, table) in expected)
        {
            var entity = db.Model.FindEntityType(type);
            Assert.NotNull(entity);
            Assert.Equal(table.Schema, entity.GetSchema());
            Assert.Equal(table.Table, entity.GetTableName());
            Assert.All(entity.FindPrimaryKey()!.Properties, p => Assert.Equal(typeof(Guid), p.ClrType));
            Assert.All(entity.GetForeignKeys().SelectMany(x => x.Properties), p =>
                Assert.True(p.ClrType == typeof(Guid) || p.ClrType == typeof(Guid?),
                    $"{type.Name}.{p.Name} must be Guid/Guid?."));
        }
    }

    [Fact]
    public void Catalog_seed_is_canonical_and_provider_adapter_binding_is_unique()
    {
        using var db = PaymentCapabilityModel.Pol();
        var model = db.GetService<IDesignTimeModel>().Model;
        var methods = model.FindEntityType(typeof(PaymentMethod))!.GetSeedData()
            .Select(x => (string)x[nameof(PaymentMethod.Code)]!).Order().ToArray();
        Assert.Equal(["card", "installment", "promptpay"], methods);

        var options = model.FindEntityType(typeof(PaymentMethodOption))!.GetSeedData()
            .Select(x => (string)x[nameof(PaymentMethodOption.Code)]!).Order().ToArray();
        Assert.Equal(["BAY", "KBANK", "KTC", "SCB"], options);
        Assert.Empty(model.FindEntityType(typeof(PaymentProviderMethodOption))!.GetSeedData());

        var provider = model.FindEntityType(typeof(PaymentProvider))!;
        Assert.Contains(provider.GetIndexes(), x =>
            x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual([nameof(PaymentProvider.AdapterCode)]));
        Assert.Contains(provider.GetKeys(), x => x.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(PaymentProvider.Id), nameof(PaymentProvider.AdapterCode)]));
    }

    [Fact]
    public void Composite_keys_keep_option_and_policy_parent_chains_exact()
    {
        using var db = PaymentCapabilityModel.Pol();

        var option = db.Model.FindEntityType(typeof(PaymentMethodOption))!;
        Assert.Contains(option.GetForeignKeys(), fk => fk.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(PaymentMethodOption.OptionGroupId), nameof(PaymentMethodOption.PaymentMethodId)]));

        var account = db.Model.FindEntityType(typeof(MerchantProviderAccountMethod))!;
        Assert.Contains(account.GetForeignKeys(), fk => fk.Properties.Select(p => p.Name).SequenceEqual([
            nameof(MerchantProviderAccountMethod.PaymentProviderMethodId),
            nameof(MerchantProviderAccountMethod.PaymentProviderId),
            nameof(MerchantProviderAccountMethod.PaymentMethodId),
        ]));

        var userPolicy = db.Model.FindEntityType(typeof(MerchantUserPaymentMethod))!;
        Assert.Contains(userPolicy.GetForeignKeys(), fk => fk.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(MerchantUserPaymentMethod.MerchantId), nameof(MerchantUserPaymentMethod.PaymentMethodId)]));
        Assert.Contains(userPolicy.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(MerchantUserPaymentMethod.MerchantUserId), nameof(MerchantUserPaymentMethod.PaymentMethodId)]));
    }
}

public sealed class MerchantUserIdentityBoundaryTests
{
    [Fact]
    public void Applicant_is_unbound_until_approval_and_cannot_be_rebound()
    {
        var merchant = Guid.NewGuid();
        var user = User.Register("google", "subject", "user@example.com", DateTime.UtcNow);

        Assert.Equal(UserStatus.PendingApproval, user.Status);
        Assert.Null(user.MerchantId);
        Assert.Throws<ArgumentException>(() => user.Approve(Guid.Empty, DateTime.UtcNow));

        user.Approve(merchant, DateTime.UtcNow);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(merchant, user.MerchantId);
        Assert.Throws<InvalidOperationException>(() => user.Approve(Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Existing_user_model_keeps_external_identity_pair_and_has_no_admin_or_secret_shortcut()
    {
        using var db = PaymentCapabilityModel.Pol();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(User))!;
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(User.Provider), nameof(User.Subject)]));
        Assert.Contains(entity.GetCheckConstraints(), x => x.Name == "CK_Users_ActorMerchant");

        var names = typeof(User).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", names);
        Assert.DoesNotContain("PasswordHash", names);
        Assert.DoesNotContain("IsAdmin", names);
        Assert.DoesNotContain("Credential", names);
        Assert.DoesNotContain("Secret", names);
    }
}

public sealed class PaymentCapabilityOwnershipTests
{
    [Fact]
    public void Global_and_tenant_capabilities_have_one_runtime_owner_each()
    {
        using var control = PaymentCapabilityModel.ControlPlane();
        using var merchant = PaymentCapabilityModel.MerchantRuntime();
        using var users = PaymentCapabilityModel.MerchantUsers();

        Type[] global =
        [
            typeof(PaymentMethod), typeof(PaymentMethodOptionGroup), typeof(PaymentMethodOption),
            typeof(PaymentProvider), typeof(PaymentProviderMethod), typeof(PaymentProviderMethodOption),
            typeof(PaymentAuthorizationState), typeof(PaymentCapabilityMigrationConflict),
        ];
        Type[] tenant =
        [
            typeof(MerchantProviderAccountMethod), typeof(MerchantProviderAccountMethodOption),
            typeof(MerchantPaymentMethod), typeof(MerchantUserPaymentMethod),
        ];

        Assert.All(global, type =>
        {
            Assert.NotNull(control.Model.FindEntityType(type));
            Assert.Null(merchant.Model.FindEntityType(type));
            Assert.Null(users.Model.FindEntityType(type));
        });
        Assert.All(tenant, type =>
        {
            Assert.Null(control.Model.FindEntityType(type));
            Assert.NotNull(merchant.Model.FindEntityType(type));
            Assert.Null(users.Model.FindEntityType(type));
        });
    }

    [Fact]
    public void Every_tenant_capability_has_filter_and_sealed_write_guard_metadata()
    {
        using var db = PaymentCapabilityModel.MerchantRuntime();
        Type[] tenant =
        [
            typeof(MerchantProviderAccountMethod), typeof(MerchantProviderAccountMethodOption),
            typeof(MerchantPaymentMethod), typeof(MerchantUserPaymentMethod),
        ];

        Assert.All(tenant, type =>
        {
            var entity = db.Model.FindEntityType(type)!;
            Assert.NotEmpty(entity.GetDeclaredQueryFilters());
            Assert.Equal(nameof(MerchantPaymentMethod.MerchantId), entity.FindAnnotation("Pol:TenantKey")?.Value);
        });
        Assert.True(typeof(MerchantRuntimeDbContext).IsSealed);
        Assert.True(typeof(ControlPlaneDbContext).IsSealed);
    }
}

internal static class PaymentCapabilityModel
{
    public static PolDbContext Pol() => new(
        new DbContextOptionsBuilder<PolDbContext>().UseSqlite("Data Source=:memory:")
            .EnableServiceProviderCaching(false).Options,
        new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
            typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
            typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
            typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
            typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
            typeof(Admins.Infrastructure.AdminModuleRegistration).Assembly,
            typeof(Iam.Infrastructure.IamModuleRegistration).Assembly,
            typeof(Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
            typeof(Levels.Infrastructure.LevelsModuleRegistration).Assembly,
            typeof(Offices.Infrastructure.OfficesModuleRegistration).Assembly,
            typeof(Positions.Infrastructure.PositionsModuleRegistration).Assembly,
            typeof(Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
        ]));

    public static ControlPlaneDbContext ControlPlane() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite("Data Source=:memory:")
            .EnableServiceProviderCaching(false).Options,
        FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    public static MerchantRuntimeDbContext MerchantRuntime() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite("Data Source=:memory:")
            .EnableServiceProviderCaching(false).Options,
        FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    public static MerchantUserDbContext MerchantUsers() => new(
        new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite("Data Source=:memory:")
            .EnableServiceProviderCaching(false).Options,
        FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
}
