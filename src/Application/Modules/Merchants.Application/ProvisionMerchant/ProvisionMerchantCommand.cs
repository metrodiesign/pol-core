using System.Text.Json;
using Mediator;
using Merchants.Domain;

namespace Merchants.Application.ProvisionMerchant;

/// <summary>The merchant config the admin submits (reference 2.4 <c>merchant.*</c>). Scalars are first-class;
/// the closed branding/routing/session/timezone/locale contract rides in <see cref="Metadata"/>.</summary>
public sealed record MerchantSpec(
    string Code,
    string Name,
    string? Note,
    string Country,
    string Currency,
    IReadOnlyList<string> EnabledChannels,
    MerchantMetadata Metadata);

/// <summary>One PSP connection in the submission (reference 2.4 <c>pspConnections[]</c>). <see cref="Secrets"/>
/// is write-only; <see cref="Config"/> is the non-secret config stored verbatim on the connection.</summary>
public sealed record PspConnectionSpec(
    string Psp,
    IReadOnlyList<string> EnabledMethods,
    string? MerchantId,
    IReadOnlyDictionary<string, string> Secrets,
    JsonElement? Config);

/// <summary>
/// Admin-driven merchant provisioning (reference 2.4). NOT <c>IMerchantScoped</c> — it is cross-merchant and
/// runs through the <c>IProvisioningWriter</c> cross-context UoW (task 8.5.4). <see cref="AdminSubject"/> and
/// <see cref="CorrelationId"/> are populated server-side from the authenticated request, never from the JSON
/// body. <see cref="CallerAdminId"/>/<see cref="ExpectedAuthorizationVersion"/> are the caller's identity +
/// authorization snapshot, pinned at the request boundary (read fresh from the admin store right before
/// dispatch) and re-verified in-transaction by the coordinator — never trusted from an earlier check alone.
/// </summary>
public sealed record ProvisionMerchantCommand(
    MerchantSpec Merchant,
    IReadOnlyList<PspConnectionSpec> PspConnections,
    string AdminSubject,
    string CorrelationId,
    Guid CallerAdminId,
    long ExpectedAuthorizationVersion) : ICommand<ProvisionMerchantResult>;
