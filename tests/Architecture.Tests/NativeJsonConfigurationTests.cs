using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Provisioning;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUsers;
using CartItem = Carts.Domain.Items.Item;
using OrderItem = Orders.Domain.Items.Item;

namespace Architecture.Tests;

public sealed class NativeJsonConfigurationTests
{
    private static readonly ModuleAssemblies Modules = new([
        typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
        typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
        typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
        typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly,
        typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly,
        typeof(global::Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
        typeof(global::Levels.Infrastructure.LevelsModuleRegistration).Assembly,
        typeof(global::Offices.Infrastructure.OfficesModuleRegistration).Assembly,
        typeof(global::Positions.Infrastructure.PositionsModuleRegistration).Assembly,
    ]);

    private const string Connection =
        "Server=localhost;Database=pol_model_only;User Id=model;Password=not-a-secret;TrustServerCertificate=True";

    [Fact]
    public void Migration_owner_maps_exactly_the_five_approved_native_json_columns()
    {
        using var db = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(Connection, sql => sql.UseCompatibilityLevel(170)).Options,
            Modules);

        AssertJson(db, typeof(ProvisioningOperation), nameof(ProvisioningOperation.Result));
        AssertJson(db, typeof(MerchantUserOutbox), nameof(MerchantUserOutbox.Payload));
        AssertJson(db, typeof(Merchant), nameof(Merchant.Metadata));
        AssertJson(db, typeof(CartItem), nameof(CartItem.Metadata));
        AssertJson(db, typeof(OrderItem), nameof(OrderItem.Metadata));

        var actual = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => string.Equals(property.GetColumnType(), "json", StringComparison.OrdinalIgnoreCase))
            .Select(property => $"{property.DeclaringType.ClrType.Name}.{property.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(5, actual.Length);

        Assert.NotEqual("json", ColumnType(db, typeof(Payments.Domain.Psp.Connection), "Metadata"));
        Assert.NotEqual("json", ColumnType(db, typeof(OutboxMessage), nameof(OutboxMessage.Payload)));
    }

    [Fact]
    public void Runtime_context_mirrors_use_native_json_for_owned_columns()
    {
        using var controlPlane = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(Connection, sql => sql.UseCompatibilityLevel(170)).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using var merchantUsers = new MerchantUserDbContext(
            new DbContextOptionsBuilder<MerchantUserDbContext>()
                .UseSqlServer(Connection, sql => sql.UseCompatibilityLevel(170)).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using var merchantRuntime = new MerchantRuntimeDbContext(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                .UseSqlServer(Connection, sql => sql.UseCompatibilityLevel(170)).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        AssertJson(controlPlane, typeof(ProvisioningOperation), nameof(ProvisioningOperation.Result));
        AssertJson(merchantUsers, typeof(MerchantUserOutbox), nameof(MerchantUserOutbox.Payload));
        AssertJson(merchantRuntime, typeof(Merchant), nameof(Merchant.Metadata));
        AssertJson(merchantRuntime, typeof(CartItem), nameof(CartItem.Metadata));
        AssertJson(merchantRuntime, typeof(OrderItem), nameof(OrderItem.Metadata));
    }

    [Fact]
    public void Cart_generic_contract_has_matching_migration_owner_and_runtime_shape()
    {
        using var owner = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(Connection, sql => sql.UseCompatibilityLevel(170)).Options,
            Modules);
        using var runtime = new MerchantRuntimeDbContext(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                .UseSqlServer(Connection, sql => sql.UseCompatibilityLevel(170)).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        AssertCartShape(owner);
        AssertCartShape(runtime);
    }

    [Fact]
    public void Every_production_UseSqlServer_call_sets_provider_compatibility_170()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();
        var callCount = 0;

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(repoRoot, "src"), "*.cs", System.IO.SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var cursor = 0;
            while ((cursor = text.IndexOf(".UseSqlServer(", cursor, StringComparison.Ordinal)) >= 0)
            {
                callCount++;
                var end = text.IndexOf(".Options", cursor, StringComparison.Ordinal);
                var call = end < 0 ? text[cursor..] : text[cursor..end];
                if (!call.Contains("UseCompatibilityLevel(170)", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(repoRoot, file));
                cursor += ".UseSqlServer(".Length;
            }
        }

        Assert.True(callCount > 0);
        Assert.True(offenders.Count == 0,
            "UseSqlServer call missing UseCompatibilityLevel(170): " + string.Join(", ", offenders.Distinct()));
    }

    private static void AssertJson(DbContext db, Type entityType, string property) =>
        Assert.Equal("json", ColumnType(db, entityType, property));

    private static void AssertCartShape(DbContext db)
    {
        var cart = db.Model.FindEntityType(typeof(Carts.Domain.Cart))!;
        var item = db.Model.FindEntityType(typeof(CartItem))!;

        Assert.Equal(20, cart.FindProperty(nameof(Carts.Domain.Cart.SaleCode))!.GetMaxLength());
        Assert.Equal(150, item.FindProperty(nameof(CartItem.ProductCode))!.GetMaxLength());
        Assert.Equal(64, item.FindProperty(nameof(CartItem.VariantCode))!.GetMaxLength());
        Assert.Equal(128, item.FindProperty(nameof(CartItem.VariantName))!.GetMaxLength());
        Assert.Null(item.FindProperty("DocumentNo"));
        Assert.Null(item.FindProperty("ProductGroup"));
    }

    private static string? ColumnType(DbContext db, Type entityType, string property) =>
        db.Model.FindEntityType(entityType)?.FindProperty(property)?.GetColumnType();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
