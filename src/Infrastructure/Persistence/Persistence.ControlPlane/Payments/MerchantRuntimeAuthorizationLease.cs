using System.Data;
using System.Data.Common;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Payments.Application.AdminControlPlane;
using Persistence.ControlPlane;

namespace Persistence.ControlPlane.Payments;

/// <summary>
/// Verifies the admin authorization lease through one conditional update in the caller's existing
/// transaction. The runtime model does not add a CLR projection or a second mapping owner for admin.Users:
/// the predicate both checks the snapshot and takes the row lock, so revoke-before-commit and
/// business-before-revoke have one deterministic order.
/// </summary>
internal sealed class MerchantRuntimeAuthorizationLease(
    ControlPlaneDbContext db,
    ISecurityTelemetry telemetry) : IMerchantRuntimeAuthorizationLease
{
    private const int ActiveStatus = 1;

    public async Task VerifyAsync(AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Authorization lease requires an active Merchant Runtime transaction.");

        var affected = await PlatformReadGuard.ReadAsync(
            ct => ExecuteConditionalLeaseUpdateAsync(access, ct), cancellationToken);
        if (affected == 1)
            return;

        telemetry.Emit(new DenialEvent(
            DenialCategory.AdminRevalidationDenial, "admin", access.ActorId, TargetMerchant: null,
            "AdminAuthorizationLease", "MerchantRuntimeAuthorizationLease.Verify",
            "Admin authorization lease was stale or inactive.", CorrelationId.Current, DateTime.UtcNow));
        throw new AccessDeniedException("Admin authorization changed; refresh the session.", "authorization_stale");
    }

    private async Task<int> ExecuteConditionalLeaseUpdateAsync(
        AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var isSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        command.CommandText = isSqlite
            ? "UPDATE \"Users\" SET \"AuthorizationVersion\" = \"AuthorizationVersion\" " +
              "WHERE \"Id\" = @lease_id AND \"Status\" = @active_status AND \"AuthorizationVersion\" = @expected_version"
            : "UPDATE [admin].[Users] SET [AuthorizationVersion] = [AuthorizationVersion] " +
              "WHERE [Id] = @lease_id AND [Status] = @active_status AND [AuthorizationVersion] = @expected_version";

        AddParameter(command, "@lease_id", DbType.Guid, access.ActorId);
        AddParameter(command, "@active_status", DbType.Int32, ActiveStatus);
        AddParameter(command, "@expected_version", DbType.Int64, access.AuthorizationVersion);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
