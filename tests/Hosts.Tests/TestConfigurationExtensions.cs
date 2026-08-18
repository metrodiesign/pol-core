using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Hosts.Tests;

internal static class TestConfigurationExtensions
{
    public static IConfigurationBuilder IgnoreMachineLocalDevelopmentSettings(this IConfigurationBuilder builder)
    {
        foreach (var source in builder.Sources
                     .OfType<JsonConfigurationSource>()
                     .Where(source => string.Equals(
                         Path.GetFileName(source.Path),
                         "appsettings.Development.json",
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            builder.Sources.Remove(source);

        return builder;
    }
}
