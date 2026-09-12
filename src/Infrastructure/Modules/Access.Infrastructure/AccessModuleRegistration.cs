using Microsoft.Extensions.DependencyInjection;

namespace Access.Infrastructure;

/// <summary>Marker and registration hook for Access-owned mappings.</summary>
public static class AccessModuleRegistration
{
    public static IServiceCollection AddAccessModule(this IServiceCollection services) => services;
}
