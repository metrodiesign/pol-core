using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;

namespace Persistence.MerchantRuntime;

/// <summary>
/// Wraps every pure read this assembly issues on the request path (probe-dependency-failure-mapping
/// REQ-1.1, 1.2): a <see cref="DbException"/> — connection, TLS/pre-login, timeout, login or permission
/// failure — becomes <see cref="DependencyUnavailableException"/> (HTTP 503) instead of falling into the
/// opaque-500 bucket. Reads ONLY: <c>SaveChanges</c>/<c>BeginTransaction</c>/<c>Commit</c> never route
/// through here, and <c>DbUpdateException</c> does not derive from <see cref="DbException"/>, so a write
/// failure can never be re-labelled retryable by construction (REQ-1.5).
/// </summary>
internal static class PlatformReadGuard
{
    public static async Task<T> ReadAsync<T>(
        Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // A cancelled command surfaces as a SqlException too (same order as SpDocumentGateway) — REQ-1.4.
            cancellationToken.ThrowIfCancellationRequested();
            throw new DependencyUnavailableException(Describe(ex), ex);
        }
    }

    // REQ-4.1: Number/State/Class composed into the message directly — SqlClient's ToString() appends them
    // conditionally, which is not deterministic for transport failures. The message never reaches the
    // client (the handler's 503 detail is fixed — REQ-3.3); numbers only, no credentials (REQ-4.4).
    private static string Describe(DbException ex) => ex is SqlException sql
        ? $"A platform database read failed (SQL error {sql.Number}, state {sql.State}, class {sql.Class})."
        : "A platform database read failed.";
}
