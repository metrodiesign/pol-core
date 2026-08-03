using Orders.Application;

namespace Hosts.Tests;

/// <summary>
/// Order numbers without a SQL sequence — the E2E suites here run on SQLite, which has none. Hands out
/// ORD69000000NN in call order, the shape <c>OrderNoSequence.Format</c> produces, and each instance starts
/// from 1 so one per test class is one independent run of numbers (the UNIQUE index is per in-memory DB).
/// </summary>
internal sealed class StubOrderNoSequence : IOrderNoSequence
{
    private int _next;

    public Task<string> NextAsync(CancellationToken cancellationToken) =>
        Task.FromResult($"ORD69{Interlocked.Increment(ref _next):D8}");
}
