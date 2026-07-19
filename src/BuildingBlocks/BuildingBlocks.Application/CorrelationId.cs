using System.Diagnostics;

namespace BuildingBlocks.Application;

/// <summary>
/// The one correlation id source shared by every host (Api/Worker) and every layer (HTTP request down to
/// Persistence.*) without new DI plumbing: .NET's built-in <see cref="Activity"/> tracing context, which
/// ASP.NET Core already populates per-request and the outbox dispatchers already run inside per-message
/// scopes for. Falls back to a fresh id only when nothing started an activity (e.g. a unit test).
/// </summary>
public static class CorrelationId
{
    public static string Current => Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
}
