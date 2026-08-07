using Contracts;
using Merchants.Application.Users;
using Merchants.Domain.Users;
using Merchants.Infrastructure;

namespace Merchants.Tests;

public sealed class KycPhotoLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pol-kyc-{Guid.NewGuid():N}");

    [Fact]
    public void Registration_history_types_have_no_KYC_key_surface()
    {
        Assert.Null(typeof(RegistrationAttempt).GetProperty("KycPhotoObjectKey"));
        Assert.Null(typeof(AttemptView).GetProperty("KycPhotoObjectKey"));
    }

    [Fact]
    public async Task Local_store_staging_is_deterministic_and_commit_delete_are_idempotent()
    {
        var store = new LocalPhotoStore(_root);
        var operationId = Guid.NewGuid();
        byte[] bytes = [0xFF, 0xD8, 0xFF];

        var first = await store.PutStagedAsync(operationId, bytes, PhotoValidation.Jpeg, default);
        var retry = await store.PutStagedAsync(operationId, bytes, PhotoValidation.Jpeg, default);

        Assert.Equal(first, retry);
        Assert.True(File.Exists(Path.Combine(_root, ".staged", first)));

        await store.CommitAsync(first, default);
        await store.CommitAsync(first, default);
        Assert.False(File.Exists(Path.Combine(_root, ".staged", first)));
        Assert.True(File.Exists(Path.Combine(_root, first)));

        await store.DiscardStagedAsync(first, default);
        Assert.True(File.Exists(Path.Combine(_root, first)));

        await store.DeleteAsync(first, default);
        await store.DeleteAsync(first, default);
        Assert.False(File.Exists(Path.Combine(_root, first)));
    }

    [Fact]
    public async Task New_staging_operation_sweeps_objects_older_than_24_hours()
    {
        var store = new LocalPhotoStore(_root);
        var expiredKey = await store.PutStagedAsync(
            Guid.NewGuid(), new byte[] { 0x89, 0x50, 0x4E, 0x47 }, PhotoValidation.Png, default);
        var expiredPath = Path.Combine(_root, ".staged", expiredKey);
        File.SetLastWriteTimeUtc(expiredPath, DateTime.UtcNow - LocalPhotoStore.StagingTtl - TimeSpan.FromMinutes(1));

        await store.PutStagedAsync(
            Guid.NewGuid(), new byte[] { 0xFF, 0xD8, 0xFF }, PhotoValidation.Jpeg, default);

        Assert.False(File.Exists(expiredPath));
    }

    [Fact]
    public async Task Same_operation_rejects_different_photo_content()
    {
        var store = new LocalPhotoStore(_root);
        var operationId = Guid.NewGuid();
        await store.PutStagedAsync(
            operationId, new byte[] { 0xFF, 0xD8, 0xFF }, PhotoValidation.Jpeg, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutStagedAsync(
            operationId, new byte[] { 0xFF, 0xD8, 0xFE }, PhotoValidation.Jpeg, default));
    }

    [Fact]
    public async Task Concurrent_same_operation_writes_one_immutable_staged_object()
    {
        var store = new LocalPhotoStore(_root);
        var operationId = Guid.NewGuid();
        byte[] bytes = [0xFF, 0xD8, 0xFF];

        var keys = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.PutStagedAsync(operationId, bytes, PhotoValidation.Jpeg, default)));

        Assert.Single(keys.Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, ".staged")));
    }

    [Fact]
    public async Task Same_operation_rejects_different_declared_photo_type()
    {
        var store = new LocalPhotoStore(_root);
        var operationId = Guid.NewGuid();
        await store.PutStagedAsync(
            operationId, new byte[] { 0xFF, 0xD8, 0xFF }, PhotoValidation.Jpeg, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutStagedAsync(
            operationId, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, PhotoValidation.Png, default));
    }

    [Fact]
    public async Task Lifecycle_consumer_replay_promotes_new_and_deletes_old_without_payload_projection()
    {
        var store = new FakePhotoStore();
        var consumer = new KycPhotoLifecycleConsumer(store);
        var notification = new KycPhotoLifecycleRequested("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.jpg",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg");

        await consumer.Handle(notification, default);
        await consumer.Handle(notification, default);

        Assert.Equal(2, store.CommitCalls);
        Assert.Equal(2, store.DeleteCalls);
    }

    [Fact]
    public async Task Lifecycle_consumer_does_not_expose_provider_error_containing_object_key()
    {
        const string key = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.jpg";
        var store = new FakePhotoStore { Failure = new IOException($"provider failed for /bucket/{key}") };
        var consumer = new KycPhotoLifecycleConsumer(store);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.Handle(new KycPhotoLifecycleRequested(key, null), default).AsTask());

        Assert.DoesNotContain(key, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakePhotoStore : IPhotoStore
    {
        public int CommitCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public Exception? Failure { get; init; }

        public Task<string> PutAsync(byte[] bytes, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<(byte[] Bytes, string ContentType)?> GetAsync(string objectKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PutStagedAsync(Guid operationId, ReadOnlyMemory<byte> bytes, string contentType,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CommitAsync(string objectKey, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                return Task.FromException(Failure);
            CommitCalls++;
            return Task.CompletedTask;
        }
        public Task DiscardStagedAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }
    }
}
