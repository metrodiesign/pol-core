using Microsoft.Extensions.DependencyInjection;

namespace Accounts.Infrastructure;

/// <summary>Marker and registration hook for Account-owned mappings.</summary>
public static class AccountsModuleRegistration
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services) => services;
}
