using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.Application;

namespace Persistence.Provisioning;

/// <summary>
/// Canonical native-JSON ledger payload. Contains identifiers and bounded status only; masked secret hints remain
/// response-only and are reconstructed from the hash-matched request on replay.
/// </summary>
internal static class ProvisioningLedgerResultCodec
{
    private const string Succeeded = "succeeded";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(ProvisioningWriteResult result) =>
        JsonSerializer.Serialize(new LedgerResult(
            result.MerchantId,
            Succeeded,
            [.. result.Connections.Select(c => new LedgerConnection(c.PspConnectionId, c.Psp))]), Options);

    public static ProvisioningWriteResult Deserialize(string json, ProvisionSpec request)
    {
        var stored = JsonSerializer.Deserialize<LedgerResult>(json, Options)
            ?? throw new JsonException("Stored provisioning result cannot be null.");
        if (stored.MerchantId == Guid.Empty)
            throw new JsonException("Stored provisioning result has no merchant id.");
        if (!string.Equals(stored.Status, Succeeded, StringComparison.Ordinal))
            throw new JsonException("Stored provisioning result has an unsupported status.");
        if (stored.Connections is null || stored.Connections.Count != request.Connections.Count)
            throw new JsonException("Stored provisioning result does not match its request.");

        var requestByPsp = request.Connections.ToDictionary(c => c.Psp, StringComparer.OrdinalIgnoreCase);
        var connections = new List<ProvisionedConnectionWrite>(stored.Connections.Count);
        var seenPsp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in stored.Connections)
        {
            if (connection.PspConnectionId == Guid.Empty || !seenPsp.Add(connection.Psp) ||
                !requestByPsp.TryGetValue(connection.Psp, out var requested))
                throw new JsonException("Stored provisioning result does not match its request.");
            connections.Add(new ProvisionedConnectionWrite(
                connection.PspConnectionId, connection.Psp, requested.MaskedSecretHints));
        }

        return new ProvisioningWriteResult(stored.MerchantId, connections);
    }

    private sealed record LedgerResult(
        Guid MerchantId,
        string Status,
        IReadOnlyList<LedgerConnection>? Connections);

    private sealed record LedgerConnection(Guid PspConnectionId, string Psp);
}
