using System.Text.Json;
using Contracts;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime.Outbox;
using SharedKernel;

namespace Architecture.Tests;

public sealed class PaymentLifecycleArchitectureTests
{
    [Fact]
    public void Payment_outbox_registry_pins_stable_names_and_versions()
    {
        var at = new DateTime(2026, 8, 7, 4, 0, 0, DateTimeKind.Utc);
        var paid = new PaymentPaid(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Money.Of(100m, "THB"),
            "card", "2c2p", "charge-1", "psp-event-1", at);
        var failed = new PaymentFailed(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "psp_failed", at);
        var expired = new PaymentExpired(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), at);

        AssertRegistryRoundTrip(paid, PaymentPaid.EventType, "v2");
        AssertRegistryRoundTrip(failed, PaymentFailed.EventType, "v1");
        AssertRegistryRoundTrip(expired, PaymentExpired.EventType, "v1");
    }

    [Fact]
    public void Payment_outbox_registry_rejects_unknown_version_and_unknown_payload_fields()
    {
        var at = new DateTime(2026, 8, 7, 4, 0, 0, DateTimeKind.Utc);
        var notification = new PaymentExpired(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), at);
        var serialized = MerchantRuntimeOutboxEventRegistry.Serialize(notification, at);

        Assert.Throws<InvalidOperationException>(() => MerchantRuntimeOutboxEventRegistry.Deserialize(
            serialized.EventType, "v999", serialized.Payload));

        var withUnknown = serialized.Payload.TrimEnd('}') + ",\"secret\":\"must-reject\"}";
        Assert.Throws<JsonException>(() => MerchantRuntimeOutboxEventRegistry.Deserialize(
            serialized.EventType, serialized.SchemaVersion, withUnknown));
    }

    [Fact]
    public void PaymentConfirmationService_is_only_application_emitter_of_failed_and_expired_transitions()
    {
        var root = FindRepoRoot();
        var applicationRoot = Path.Combine(root, "src/Modules/Payments/Payments.Application");
        var offenders = Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith("PaymentConfirmationService.cs", StringComparison.Ordinal))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains(".MarkFailed(", StringComparison.Ordinal)
                    || text.Contains(".MarkExpired(", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_Order_lifecycle_writer_uses_shared_locked_read_primitive()
    {
        var root = FindRepoRoot();
        var lockedWriters = new[]
        {
            "src/Modules/Orders/Orders.Application/CancelOrder.cs",
            "src/Modules/Orders/Orders.Application/OrderPaidConsumer.cs",
            "src/Modules/Orders/Orders.Application/OrderPaymentFailedConsumer.cs",
            "src/Modules/Orders/Orders.Application/OrderPaymentExpiredConsumer.cs",
        };

        foreach (var relative in lockedWriters)
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            Assert.Contains(".GetForUpdateAsync(", text, StringComparison.Ordinal);
            Assert.Contains(".ExecuteInTransactionAsync(", text, StringComparison.Ordinal);
        }

        var attach = File.ReadAllText(Path.Combine(
            root, "src/Modules/Payments/Payments.Application/CreateSession/CreateSessionHandler.cs"));
        Assert.Contains(".GetForMintAsync(", attach, StringComparison.Ordinal);
        Assert.Contains(".AttachAttemptAsync(", attach, StringComparison.Ordinal);

        var orderRepository = File.ReadAllText(Path.Combine(
            root, "src/Persistence/Persistence.MerchantRuntime/Orders/OrderRepository.cs"));
        var payableOrderReader = File.ReadAllText(Path.Combine(
            root, "src/Persistence/Persistence.MerchantRuntime/Payments/PayableOrderReader.cs"));
        Assert.Contains("WITH (UPDLOCK,HOLDLOCK)", orderRepository, StringComparison.Ordinal);
        Assert.Contains("WITH (UPDLOCK,HOLDLOCK)", payableOrderReader, StringComparison.Ordinal);
        Assert.Contains("MerchantId = @p1", orderRepository, StringComparison.Ordinal);
        Assert.Contains("MerchantId = @p1", payableOrderReader, StringComparison.Ordinal);
    }

    private static void AssertRegistryRoundTrip<T>(T notification, string eventType, string schemaVersion)
        where T : Mediator.INotification
    {
        var serialized = MerchantRuntimeOutboxEventRegistry.Serialize(notification, DateTime.UnixEpoch);
        Assert.Equal(eventType, serialized.EventType);
        Assert.Equal(schemaVersion, serialized.SchemaVersion);
        Assert.Equal(notification, Assert.IsType<T>(MerchantRuntimeOutboxEventRegistry.Deserialize(
            serialized.EventType, serialized.SchemaVersion, serialized.Payload)));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
