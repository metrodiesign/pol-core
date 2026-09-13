using System.Text.Json;
using SharedKernel;

namespace BuildingBlocks.Infrastructure.Outbox;

/// <summary>Shared JSON options for outbox payloads — camelCase + <see cref="MoneyJsonConverter"/>.</summary>
public static class OutboxSerializer
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter());
        return options;
    }
}
