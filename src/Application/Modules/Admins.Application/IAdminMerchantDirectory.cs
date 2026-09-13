namespace Admins.Application;

/// <summary>
/// Read-only merchant checks the admin module needs, implemented in the HOST over the pol_admin (bypass)
/// connection so Admins.Application stays free of a Merchant-module dependency (mirrors Identity's
/// <c>IMerchantDirectory</c>). <see cref="IsActiveMerchantAsync"/> validates a merchant at assignment time (REQ-4.3);
/// <see cref="GetCodesByIdsAsync"/> maps a Scoped admin's assigned ids to SPA-friendly codes for <c>GET
/// /admin/me</c> (REQ-13.3); <see cref="GetIdByCodeAsync"/> is the cheap code-&gt;id lookup the cross-merchant
/// read seam uses to apply the accessible-merchant floor BEFORE loading a full merchant projection (REQ-7.1).
/// </summary>
public interface IAdminMerchantDirectory
{
    Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken);
    Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken);
}
