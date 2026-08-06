using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;

namespace Architecture.Tests;

/// <summary>
/// Unit proof of <see cref="PlatformReadGuard"/>'s classification contract (probe-dependency-failure-mapping
/// REQ-1.1, 1.4, 1.5, 4.1): a <see cref="DbException"/> from a read becomes
/// <see cref="DependencyUnavailableException"/> with the original as inner and Number/State/Class in the
/// message; a cancelled request is never re-labelled a dependency failure; and everything that is NOT a
/// <see cref="DbException"/> — including <see cref="DbUpdateException"/>, the write-failure shape — passes
/// through untouched, so a write can never be reported retryable.
/// </summary>
public sealed class PlatformReadGuardTests
{
    [Fact]
    public async Task A_successful_read_returns_its_value_untouched()
    {
        var value = await PlatformReadGuard.ReadAsync(_ => Task.FromResult(42), CancellationToken.None);

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task A_DbException_becomes_DependencyUnavailable_with_the_original_as_inner()
    {
        var original = new FakeDbException("connection refused");

        var wrapped = await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            PlatformReadGuard.ReadAsync<int>(_ => Task.FromException<int>(original), CancellationToken.None));

        Assert.Same(original, wrapped.InnerException);
        Assert.Equal("A platform database read failed.", wrapped.Message);
    }

    [Fact]
    public async Task A_SqlException_message_carries_number_state_and_class()
    {
        var original = SqlExceptionFactory.Create(number: 18456, state: 1, errorClass: 14);

        var wrapped = await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            PlatformReadGuard.ReadAsync<int>(_ => Task.FromException<int>(original), CancellationToken.None));

        Assert.Equal("A platform database read failed (SQL error 18456, state 1, class 14).", wrapped.Message);
        Assert.Same(original, wrapped.InnerException);
    }

    [Fact]
    public async Task A_cancelled_read_is_not_a_dependency_failure()
    {
        // A cancelled command surfaces as a provider DbException too — the guard must re-check the token
        // and let cancellation win (REQ-1.4), exactly like SpDocumentGateway does for the upstream.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PlatformReadGuard.ReadAsync<int>(
                _ => Task.FromException<int>(new FakeDbException("cancelled command")), cts.Token));
    }

    [Fact]
    public async Task A_non_DbException_passes_through_unwrapped()
    {
        // The collation guard's InvalidOperationException keeps its own meaning (its own decision, post-D1).
        var original = new InvalidOperationException("collation disagreement");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PlatformReadGuard.ReadAsync<int>(_ => Task.FromException<int>(original), CancellationToken.None));

        Assert.Same(original, thrown);
    }

    [Fact]
    public async Task A_write_failure_shape_can_never_be_wrapped_even_if_misplaced()
    {
        // DbUpdateException does not derive from DbException, so even a guard wrapped around a write by
        // mistake cannot re-label the failure retryable (REQ-1.5, by construction).
        Assert.False(typeof(DbException).IsAssignableFrom(typeof(DbUpdateException)));

        var original = new DbUpdateException("save failed");

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() =>
            PlatformReadGuard.ReadAsync<int>(_ => Task.FromException<int>(original), CancellationToken.None));

        Assert.Same(original, thrown);
    }

    private sealed class FakeDbException : DbException
    {
        public FakeDbException(string message) : base(message) { }
    }
}
