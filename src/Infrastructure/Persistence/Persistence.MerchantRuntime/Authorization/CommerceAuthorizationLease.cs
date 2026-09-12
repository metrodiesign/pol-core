using System.Data;
using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Orders.Application;

namespace Persistence.MerchantRuntime.Authorization;

/// <summary>Revalidates an identity proof on the Commerce connection that owns the business transaction.
/// The no-op Account update takes an update/range lock until commit, so a control-plane revoke cannot commit
/// between authorization and the order/idempotency write.</summary>
internal sealed class CommerceAuthorizationLease(
    CommerceDbContext db,
    ISecurityTelemetry telemetry) : ICommerceAuthorizationLease
{
    private const int ActiveAccount = 1;
    private const int ActiveMerchantAccess = 1;
    private const int ActiveSystemClient = 1;
    private const int ActiveRole = 1;

    public async Task VerifyAsync(
        CommerceAuthorizationProof? proof,
        CancellationToken cancellationToken)
    {
        if (proof is null)
            return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Commerce authorization lease requires an active transaction.");

        ValidateProof(proof);
        var affected = await ExecuteConditionalLeaseUpdateAsync(proof, cancellationToken)
            .ConfigureAwait(false);
        if (affected == 1)
            return;

        telemetry.Emit(new DenialEvent(
            DenialCategory.AdminRevalidationDenial,
            "identity",
            proof.AccountId,
            proof.MerchantId,
            "AccountAuthorizationLease",
            "CommerceAuthorizationLease.Verify",
            "Identity authorization lease was stale, inactive, or missing.",
            CorrelationId.Current,
            DateTime.UtcNow));
        throw new AccessDeniedException(
            "Identity authorization changed; refresh the session.", "authorization_stale");
    }

    private async Task<int> ExecuteConditionalLeaseUpdateAsync(
        CommerceAuthorizationProof proof,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = """
            UPDATE a WITH (UPDLOCK, HOLDLOCK)
            SET AuthorizationVersion = AuthorizationVersion
            FROM [acct].[Accounts] AS a
            WHERE a.Id = @account_id
              AND a.Status = @active_account
              AND a.AuthorizationVersion = @expected_version
              AND EXISTS (
                  SELECT 1
                  FROM [access].[MerchantAccess] AS ma
                  WHERE ma.AccountId = a.Id
                    AND ma.MerchantId = @merchant_id
                    AND ma.Status = @active_access
                    AND (
                        (
                            @system_client_id IS NULL
                            AND @required_permission IS NOT NULL
                            AND EXISTS (
                                SELECT 1
                                FROM [access].[AccessRoles] AS ar
                                INNER JOIN [iam].[Roles] AS r ON r.Id = ar.RoleId
                                INNER JOIN [iam].[RolePermissions] AS rp ON rp.RoleId = r.Id
                                WHERE ar.MerchantAccessId = ma.Id
                                  AND ar.MerchantId = ma.MerchantId
                                  AND r.Status = @active_role
                                  AND (r.MerchantId IS NULL OR r.MerchantId = @merchant_id)
                                  AND rp.PermissionKey = @required_permission
                            )
                        )
                        OR
                        (
                            @system_client_id IS NOT NULL
                            AND @required_scope IS NOT NULL
                            AND EXISTS (
                                SELECT 1
                                FROM [acct].[SystemClients] AS sc
                                INNER JOIN [access].[SystemClientScopes] AS ss
                                    ON ss.SystemClientId = sc.Id
                                WHERE sc.AccountId = a.Id
                                  AND sc.ClientId = @system_client_id
                                  AND sc.MerchantId = @merchant_id
                                  AND sc.Status = @active_system_client
                                  AND ss.ScopeCode = @required_scope
                            )
                        )
                    )
              );
            """;

        AddParameter(command, "@account_id", DbType.Guid, proof.AccountId);
        AddParameter(command, "@expected_version", DbType.Int64, proof.ExpectedAuthorizationVersion);
        AddParameter(command, "@merchant_id", DbType.Guid, proof.MerchantId);
        AddParameter(command, "@active_account", DbType.Int32, ActiveAccount);
        AddParameter(command, "@active_access", DbType.Int32, ActiveMerchantAccess);
        AddParameter(command, "@active_system_client", DbType.Int32, ActiveSystemClient);
        AddParameter(command, "@active_role", DbType.Int32, ActiveRole);
        AddParameter(command, "@system_client_id", DbType.String, proof.SystemClientId);
        AddParameter(command, "@required_permission", DbType.String, proof.RequiredPermission);
        AddParameter(command, "@required_scope", DbType.String, proof.RequiredScope);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateProof(CommerceAuthorizationProof proof)
    {
        if (proof.AccountId == Guid.Empty || proof.MerchantId == Guid.Empty
            || proof.ExpectedAuthorizationVersion < 0)
            throw new AccessDeniedException(
                "Identity authorization proof is invalid.", "authorization_invalid");

        var system = !string.IsNullOrWhiteSpace(proof.SystemClientId);
        var human = !string.IsNullOrWhiteSpace(proof.RequiredPermission);
        if (system == human
            || (system && string.IsNullOrWhiteSpace(proof.RequiredScope))
            || (human && !string.IsNullOrWhiteSpace(proof.RequiredScope)))
            throw new AccessDeniedException(
                "Identity authorization proof is invalid.", "authorization_invalid");
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
