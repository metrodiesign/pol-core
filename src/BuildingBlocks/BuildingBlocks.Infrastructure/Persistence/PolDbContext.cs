using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The one DbContext for the whole platform (T1 — one connection = one SESSION_CONTEXT stamp = one
/// transaction = one migration history). Every entity maps its own schema explicitly via
/// <c>ToTable(name, schema)</c> — there is no <c>HasDefaultSchema</c> (rf1 multi-schema layout,
/// REQ-1.2). Module entity mappings are discovered at model-build time from
/// <see cref="ModuleAssemblies.Modules"/>, so this context keeps no compile-time dependency on any
/// module. The DB catalog itself stays named <c>VCentralPay</c> (T2) — only the connection string
/// names it; nothing in code references the catalog name.
/// </summary>
public sealed class PolDbContext : DbContext
{
    private readonly ModuleAssemblies _modules;

    public PolDbContext(DbContextOptions<PolDbContext> options, ModuleAssemblies modules)
        : base(options)
        => _modules = modules;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<MerchantUserOutbox> MerchantUserOutbox => Set<MerchantUserOutbox>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<VaultSecretBlob> VaultSecrets => Set<VaultSecretBlob>();
    public DbSet<VaultRevealAudit> VaultRevealAudits => Set<VaultRevealAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new MerchantUserOutboxConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new VaultSecretBlobConfiguration());
        modelBuilder.ApplyConfiguration(new VaultRevealAuditConfiguration());
        modelBuilder.ApplyConfiguration(new DataProtectionKeyConfiguration());

        foreach (var assembly in _modules.Modules)
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);

        base.OnModelCreating(modelBuilder);
    }
}
