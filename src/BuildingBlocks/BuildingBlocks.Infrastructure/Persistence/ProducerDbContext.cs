using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The producer-side data plane (schema <c>producer</c>) shared by tenant-facing modules. Owns the
/// cross-cutting outbox, idempotency and vault tables; module entity mappings are discovered at
/// model-build time from <see cref="ModuleAssemblies.Producer"/>, so this context keeps no
/// compile-time dependency on any module.
/// </summary>
public sealed class ProducerDbContext : DbContext
{
    public const string Schema = "producer";

    private readonly ModuleAssemblies _modules;

    public ProducerDbContext(DbContextOptions<ProducerDbContext> options, ModuleAssemblies modules)
        : base(options)
        => _modules = modules;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<VaultSecretBlob> VaultSecrets => Set<VaultSecretBlob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new VaultSecretBlobConfiguration());

        foreach (var assembly in _modules.Producer)
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);

        base.OnModelCreating(modelBuilder);
    }
}
