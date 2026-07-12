using Admins.Application.ResolveAdmin;

namespace Admins.Application;

/// <summary>
/// Per-request holder of the resolved admin (REQ-6.3): the admin-resolution middleware materializes the
/// <see cref="AdminResolution"/> once and every downstream reader — the <c>IAdminQuery</c> scoped floor, the
/// approve scope check, <c>GET /admin/me</c> — reads it without re-resolving. The host owns the implementation
/// (the write happens in middleware); this read-only seam is what handlers/endpoints depend on.
/// </summary>
public interface IAdminScope
{
    bool IsBound { get; }

    /// <summary>The resolved admin. Throws if no admin is bound (an admin-policy route always binds first).</summary>
    AdminResolution Current { get; }

    /// <summary>Convenience accessor for <see cref="AdminResolution.Accessible"/> — the floor's decision input.</summary>
    AccessibleMerchants Accessible { get; }
}
