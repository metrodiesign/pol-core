using Governance.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Persistence.ControlPlane.Governance;

public static class AuditAnchorRegistration
{
    public static IServiceCollection AddGovernanceAuditAnchoring(this IServiceCollection services)
    {
        services.AddSingleton<IAuditAnchorStore>(sp => CreateStore(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>()));
        services.AddSingleton<AuditAnchorHealthState>();
        services.AddHostedService<AuditAnchorService>();
        services.AddHealthChecks()
            .AddCheck<AuditAnchorReadinessCheck>("audit-anchor", tags: ["ready"]);
        return services;
    }

    private static IAuditAnchorStore CreateStore(IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration["AuditAnchor:Path"]?.Trim();
        var signingKeyFile = configuration["AuditAnchor:SigningKeyFile"]?.Trim();
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasKey = !string.IsNullOrWhiteSpace(signingKeyFile);
        if (hasPath != hasKey)
            throw new InvalidOperationException(
                "AuditAnchor:Path and AuditAnchor:SigningKeyFile must be configured together.");

        var required = !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
        if (required && !hasPath)
            throw new InvalidOperationException(
                "External signed audit anchoring is required outside Development/Testing.");

        if (hasPath)
        {
            if (!System.IO.Path.IsPathFullyQualified(path!)
                || !System.IO.Path.IsPathFullyQualified(signingKeyFile!))
                throw new InvalidOperationException("Audit anchor and signing-key paths must be absolute.");
            return new FileAuditAnchorStore(new AuditAnchorOptions(path!, signingKeyFile!));
        }

        return new DisabledAuditAnchorStore();
    }
}
