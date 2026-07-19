using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// rls-to-query-filter REQ-4.9/REQ-2-v7#1/#4: the in-transaction authorization lease — a lease-covered
/// ControlPlane write (Reactivate/RevokeAdminSession/Suspend/SetAdminRoles, plus provisioning's own copy of
/// this pattern, task 7) verifies the CALLER's <see cref="Admins.Domain.Users.User.AuthorizationVersion"/>
/// still matches the snapshot pinned at the request boundary, in the SAME transaction as the business
/// write, so a caller whose authorization was revoked/demoted AFTER they authenticated cannot still commit.
/// <para>
/// Two layers, both load-bearing: (1) the explicit version comparison below catches a snapshot that is
/// ALREADY stale the moment this transaction starts (no race needed — a revoke committed before this
/// transaction even began); (2) forcing <see cref="AuthorizationVersion"/> into the SaveChanges batch as a
/// concurrency token (<c>Persistence.ControlPlane.Admins.UserConfiguration</c>) catches a revoke that races
/// in DURING this transaction — the WHERE clause EF emits carries the version this lease just verified, so
/// a concurrent bump makes the eventual UPDATE affect zero rows and throw
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> at commit. Distinct from a
/// BUSINESS write's row-count (e.g. session-family revoke, 0..N and idempotent) — that is never an
/// authorization signal, only this explicit lease is.
/// </para>
/// </summary>
internal static class AuthorizationLease
{
    public static async Task VerifyAsync(
        ControlPlaneDbContext db, Guid callerId, long expectedVersion, ISecurityTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        var caller = await db.Users.SingleOrDefaultAsync(u => u.Id == callerId, cancellationToken);
        if (caller is null)
        {
            Emit(telemetry, callerId, "Authorization lease: caller was not found.");
            throw new WriteGuardException($"Authorization lease: caller '{callerId}' was not found.");
        }

        if (caller.AuthorizationVersion != expectedVersion)
        {
            Emit(telemetry, callerId, "Authorization lease: caller's authorization version is stale.");
            throw new WriteGuardException(
                $"Authorization lease: stale for caller '{callerId}' (expected version {expectedVersion}, "
                + $"actual {caller.AuthorizationVersion}).");
        }

        // A no-op assignment does not by itself make EF include the column in the UPDATE it emits — force it
        // so the concurrency-token WHERE clause actually runs at SaveChanges time (§2 above).
        db.Entry(caller).Property(u => u.AuthorizationVersion).IsModified = true;
    }

    private static void Emit(ISecurityTelemetry telemetry, Guid callerId, string reason) =>
        telemetry.Emit(new DenialEvent(
            DenialCategory.AdminRevalidationDenial, "admin", callerId, TargetMerchant: null,
            nameof(User), "AuthorizationLease.Verify", reason,
            CorrelationId.Current, DateTime.UtcNow));
}
