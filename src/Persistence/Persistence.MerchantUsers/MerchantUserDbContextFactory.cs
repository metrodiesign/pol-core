using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Persistence.MerchantUsers;

/// <summary>Lets <c>dotnet ef migrations</c> construct <see cref="MerchantUserDbContext"/> without booting a
/// host (task 8.5.2, mirrors <c>Persistence.ControlPlane.ControlPlaneDbContextFactory</c>'s precedent).
/// <c>MerchantUserDbContext</c> is internal, and Api has no <c>InternalsVisibleTo</c> grant from this
/// assembly (design.md "Assembly split + Api host boundary"), so this factory cannot live in Api — it lives
/// here instead. <c>dotnet ef</c> discovers <see cref="IDesignTimeDbContextFactory{TContext}"/>
/// implementations by scanning every assembly in the startup project's dependency closure, not only the
/// startup project itself, so this works from any host that references this assembly.</summary>
public sealed class MerchantUserDbContextFactory : IDesignTimeDbContextFactory<MerchantUserDbContext>
{
    // ponytail: design-time only — real env-driven connection string is supplied via POL_DESIGN_SQL (same
    // env var HostModuleAssemblies.DesignConnectionString reads); this localhost default just lets the model
    // build for migrations.
    private static string DesignConnectionString =>
        Environment.GetEnvironmentVariable("POL_DESIGN_SQL")
        ?? "Server=localhost;Database=pol_core_design;Trusted_Connection=True;TrustServerCertificate=True";

    // Explicit interface implementation: MerchantUserDbContext is internal, so the CreateDbContext member
    // that names it as its return type cannot be a public method on this public class (CS0050) — EF Core's
    // design-time tooling invokes this through the IDesignTimeDbContextFactory<> interface regardless.
    MerchantUserDbContext IDesignTimeDbContextFactory<MerchantUserDbContext>.CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MerchantUserDbContext>()
            .UseSqlServer(DesignConnectionString, sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new MerchantUserDbContext(
            options, new DesignTimeUnboundActorContext(), new DesignTimeAllowAllAuthorizer(), NoOpSecurityTelemetry.Instance);
    }
}

/// <summary><c>dotnet ef</c> only builds the model — it never executes a query or a write through this
/// authorizer, so "allow all" is safe here (unlike any runtime registration).</summary>
internal sealed class DesignTimeAllowAllAuthorizer : IWriteAuthorizer
{
    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
}

/// <summary><c>dotnet ef</c> only builds the model — it never executes a query, so an actor that throws the
/// moment anything reads <see cref="MerchantId"/> is safe here (unlike any runtime registration, which must
/// resolve a real per-request actor).</summary>
internal sealed class DesignTimeUnboundActorContext : IActorContext
{
    public Guid MerchantId => throw new InvalidOperationException("Design-time context: no actor is ever bound.");
    public Guid? UserId => null;
    public bool HasActor => false;
}
