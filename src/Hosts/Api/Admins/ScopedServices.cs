using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports.Psp;
using Payments.Infrastructure.Persistence.Psp;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Application.Users.Permissions;
using Merchants.Infrastructure.Persistence;
using Merchants.Infrastructure.Persistence.Users;
using Merchants.Infrastructure.Persistence.Users.Roles;

namespace Api.Admins;

/// <summary>
/// Unit of work over the pol_admin (RLS-bypass) connection used by merchant provisioning. Unlike the
/// pol_app <c>EfUnitOfWork</c> it clears the change tracker at the START of every transaction attempt:
/// provisioning stages new entities (each with a fresh Guid and a UNIQUE merchant code), so a retried attempt
/// that did not clear would re-insert the previous attempt's rows and hit a duplicate-key violation.
/// Clearing makes each attempt independent (REQ-4.1). Translates the same persistence faults as EfUnitOfWork
/// so the provisioning path returns 409 (not 500) when a duplicate merchant code races past the pre-check.
/// </summary>
internal sealed class ProvisioningUnitOfWork : IUnitOfWork
{
    private readonly PolDbContext _db;

    public ProvisioningUnitOfWork(PolDbContext db) => _db = db;

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
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // SQL Server 2627/2601 = unique-violation. A duplicate merchant code that races past the
            // ExistsByCodeAsync pre-check lands here -> surface a domain conflict (409), not an opaque 500.
            throw new ConflictException(
                "A record with the same unique key already exists; the insert was rejected.", ex);
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

internal static class MerchantScopeRegistration
{
    /// <summary>
    /// Binds the merchant-provisioning seams to a keyed pol_admin <see cref="PolDbContext"/>. T5: <c>pol_admin</c>
    /// is OUT of <c>pol_rls_bypass</c> under rf1, so this connection now DOES need the
    /// <see cref="SessionContextConnectionInterceptor"/> — stamped from <see cref="AdminActorContext"/> (a
    /// DIFFERENT scoped instance from the request's default <see cref="IActorContext"/>, REQ-4.5 — resolving the
    /// admin's own identity would be chicken-and-egg if it depended on itself). The options are built INSIDE the
    /// per-scope factory (via <c>UseApplicationServiceProvider</c>) so EF still caches the model across requests —
    /// never hand-build fresh <see cref="DbContextOptions"/> per request. The keyed context is Scoped, so the UoW,
    /// PSP-connection repo, vault store, merchant repo and audit writer below all share ONE instance — therefore
    /// ONE transaction — per request (REQ-4.4). The three seams that also have a pol_app consumer are registered
    /// KEYED ("admin"); the merchant-only seams are plain.
    /// </summary>
    public static IServiceCollection AddMerchantAdminScope(this IServiceCollection services, string adminConnectionString)
    {
        services.AddScoped<AdminActorContext>();

        services.AddKeyedScoped<PolDbContext>("admin", (sp, _) =>
        {
            var interceptor = new SessionContextConnectionInterceptor(sp.GetRequiredService<AdminActorContext>());
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(adminConnectionString)
                .UseApplicationServiceProvider(sp)
                .AddInterceptors(interceptor)
                .Options;
            return new PolDbContext(options, sp.GetRequiredService<ModuleAssemblies>());
        });

        static PolDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<PolDbContext>("admin");

        services.AddKeyedScoped<IUnitOfWork>("admin", (sp, _) => new ProvisioningUnitOfWork(Admin(sp)));
        services.AddKeyedScoped<IConnectionRepository>("admin", (sp, _) => new ConnectionRepository(Admin(sp)));
        services.AddKeyedScoped<IVaultSecretStore>("admin", (sp, _) => new LocalEnvelopeVaultStore(
            Admin(sp),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<VaultKeyring>(),
            sp.GetRequiredService<IVaultRevealAuditWriter>())); // unused by StoreAsync; provisioning never reveals

        services.AddScoped<IMerchantRepository>(sp => new MerchantRepository(Admin(sp)));
        services.AddScoped<IProvisioningAuditWriter>(sp => new ProvisioningAuditWriter(Admin(sp)));

        return services;
    }
}
