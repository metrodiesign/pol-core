using Contracts;
using Merchants.Application.Users;
using Merchants.Infrastructure;

namespace Integration.Tests;

public sealed class KycPhotoLifecycleIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pol-kyc-integration-{Guid.NewGuid():N}");

    [Fact]
    public async Task Staged_photo_survives_process_boundary_and_outbox_replay_is_idempotent()
    {
        var firstProcess = new LocalPhotoStore(_root);
        var (oldKey, _) = await firstProcess.PutStagedAsync(
            Guid.NewGuid(), new byte[] { 0xFF, 0xD8, 0xFF }, PhotoValidation.Jpeg, default);
        await firstProcess.CommitAsync(oldKey, default);

        var (newKey, _) = await firstProcess.PutStagedAsync(
            Guid.NewGuid(), new byte[] { 0x89, 0x50, 0x4E, 0x47 }, PhotoValidation.Png, default);

        // New adapter instance models a worker/process restart after DB commit but before outbox delivery.
        var restartedProcess = new LocalPhotoStore(_root);
        var consumer = new KycPhotoLifecycleConsumer(restartedProcess);
        var lifecycle = new KycPhotoLifecycleRequested(newKey, oldKey);

        await consumer.Handle(lifecycle, default);
        await consumer.Handle(lifecycle, default);

        Assert.False(File.Exists(Path.Combine(_root, oldKey)));
        Assert.True(File.Exists(Path.Combine(_root, newKey)));
        Assert.False(File.Exists(Path.Combine(_root, ".staged", newKey)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
