using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Infrastructure.Persistence;
using Tenant.Application;
using Tenant.Infrastructure.Persistence;

namespace Api;

/// <summary>
/// Unit of work over the pol_admin (RLS-bypass) connection used by tenant provisioning. Unlike the
/// pol_app <c>EfUnitOfWork</c> it clears the change tracker at the START of every transaction attempt:
/// provisioning stages new entities (each with a fresh Guid and a UNIQUE tenant code) and the vault
/// store flushes mid-loop, so a retried attempt that did not clear would re-insert the previous
/// attempt's rows and hit a duplicate-key violation. Clearing makes each attempt independent (REQ-4.1).
/// </summary>
internal sealed class AdminProvisioningUnitOfWork : IUnitOfWork
{
    private readonly ProducerDbContext _db;

    public AdminProvisioningUnitOfWork(ProducerDbContext db) => _db = db;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(
                "A concurrent change to the same record was detected; the save was rejected.", ex);
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear(); // each retry attempt starts from a clean slate (see class summary)
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await operation(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }
}

internal static class TenantAdminScopeRegistration
{
    /// <summary>
    /// Binds the tenant-provisioning seams to a keyed pol_admin <see cref="ProducerDbContext"/> (no
    /// SESSION_CONTEXT interceptor — the bypass role sees every tenant). The keyed context is Scoped, so
    /// the UoW, PSP-connection repo, vault store, tenant repo and audit writer below all share ONE
    /// instance — therefore ONE transaction — per request (REQ-4.4). The three seams that also have a
    /// pol_app consumer are registered KEYED ("admin"); the tenant-only seams are plain.
    /// </summary>
    public static IServiceCollection AddTenantAdminScope(this IServiceCollection services, string adminConnectionString)
    {
        var options = new DbContextOptionsBuilder<ProducerDbContext>()
            .UseSqlServer(adminConnectionString)
            .Options;

        services.AddKeyedScoped<ProducerDbContext>("admin",
            (sp, _) => new ProducerDbContext(options, sp.GetRequiredService<ModuleAssemblies>()));

        static ProducerDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<ProducerDbContext>("admin");

        services.AddKeyedScoped<IUnitOfWork>("admin", (sp, _) => new AdminProvisioningUnitOfWork(Admin(sp)));
        services.AddKeyedScoped<IPspConnectionRepository>("admin", (sp, _) => new PspConnectionRepository(Admin(sp)));
        services.AddKeyedScoped<IVaultSecretStore>("admin", (sp, _) => new LocalEnvelopeVaultStore(
            Admin(sp),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<VaultKeyring>(),
            sp.GetRequiredService<IVaultRevealAuditWriter>())); // unused by StoreAsync; provisioning never reveals

        services.AddScoped<ITenantRepository>(sp => new TenantRepository(Admin(sp)));
        services.AddScoped<IProvisioningAuditWriter>(sp => new ProvisioningAuditWriter(Admin(sp)));

        return services;
    }
}
