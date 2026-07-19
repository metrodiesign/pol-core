using System.Data.Common;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;

namespace Persistence.Provisioning;

/// <summary>
/// rls-to-query-filter task 8.5.5: the ONE public entry point that wires <see cref="ProvisioningCoordinator"/>
/// behind <see cref="IProvisioningWriter"/> for the host. <paramref name="provisioningAuthorizer"/> is
/// supplied by the caller (a single <c>ProvisioningSuperWriteAuthorizer</c> instance, constructed once) —
/// this assembly cannot reference <c>Api.Persistence</c>, where that class lives, so it is never constructed
/// here.
/// </summary>
public static class ProvisioningRegistration
{
    public static IServiceCollection AddProvisioning(
        this IServiceCollection services,
        string connectionString,
        VaultKeyring keyring,
        IWriteAuthorizer provisioningAuthorizer)
    {
        services.AddScoped<IProvisioningWriter>(sp => new ProvisioningCoordinator(
            async ct =>
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                return (DbConnection)connection;
            },
            connection => new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlServer(connection).Options,
                provisioningAuthorizer,
                sp.GetRequiredService<ISecurityTelemetry>()),
            connection => new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlServer(connection).Options,
                UnboundActorContext.Instance,
                provisioningAuthorizer,
                sp.GetRequiredService<ISecurityTelemetry>()),
            keyring,
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ISecurityTelemetry>()));

        return services;
    }

    /// <summary>INSERT never consults the read-floor query filter, so the provisioning UoW's
    /// <see cref="MerchantRuntimeDbContext"/> needs no real actor — a fixed unbound instance is enough.</summary>
    private sealed class UnboundActorContext : IActorContext
    {
        public static readonly UnboundActorContext Instance = new();
        public Guid MerchantId => throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => false;
    }
}
