using BuildingBlocks.Application;
using Payments.Application.Ports;

namespace Tenant.Application;

// Marker seams bound by the host to the pol_admin (RLS-bypass) connection so that EVERY write in a
// provisioning request shares ONE DbContext instance and therefore ONE transaction (REQ-4.4). The
// provisioning/read-back handlers depend on these admin-specific types — never the plain pol_app
// seams — so a non-keyed seam can never leak in by accident (compile-time guarantee, not a string key).

/// <summary>The unit of work over the admin (pol_admin) connection.</summary>
public interface IAdminUnitOfWork : IUnitOfWork;

/// <summary>The vault secret store over the admin connection (INSERT-only at runtime; never reveals).</summary>
public interface IAdminVaultSecretStore : IVaultSecretStore;

/// <summary>The PSP connection repository over the admin connection (cross-tenant write/read).</summary>
public interface IAdminPspConnectionRepository : IPspConnectionRepository;
