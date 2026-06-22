using System.Text.Json;
using Mediator;

namespace Tenant.Application.ProvisionTenant;

/// <summary>The tenant config the admin submits (reference 2.4 <c>tenant.*</c>). Scalars are first-class;
/// the flexible part (branding/routing/session/timezone/locale/createdByAdmin) rides in
/// <see cref="Metadata"/> and is stored verbatim.</summary>
public sealed record TenantSpec(
    string Code,
    string DisplayName,
    string LegalEntityId,
    string Country,
    string Currency,
    IReadOnlyList<string> EnabledChannels,
    JsonElement? Metadata);

/// <summary>One PSP connection in the submission (reference 2.4 <c>pspConnections[]</c>). <see cref="Secrets"/>
/// is write-only; <see cref="Config"/> is the non-secret config stored verbatim on the connection.</summary>
public sealed record PspConnectionSpec(
    string Psp,
    IReadOnlyList<string> EnabledMethods,
    string? MerchantId,
    IReadOnlyDictionary<string, string> Secrets,
    JsonElement? Config);

/// <summary>
/// Admin-driven tenant provisioning (reference 2.4). NOT <c>ITenantScoped</c> — it is cross-tenant and
/// runs under the pol_admin connection. <see cref="AdminSubject"/> and <see cref="CorrelationId"/> are
/// populated server-side from the authenticated request, never from the JSON body.
/// </summary>
public sealed record ProvisionTenantCommand(
    TenantSpec Tenant,
    IReadOnlyList<PspConnectionSpec> PspConnections,
    string AdminSubject,
    string CorrelationId) : ICommand<ProvisionTenantResult>;
