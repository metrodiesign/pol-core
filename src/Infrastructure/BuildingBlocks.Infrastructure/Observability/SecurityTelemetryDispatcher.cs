using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure.Observability;

/// <summary>
/// The durable non-blocking half of REQ-13.4: <see cref="SecurityTelemetryChannel.Emit"/> only enqueues (a
/// bounded, non-blocking write — never awaits network I/O on the caller's write/read path); this
/// <see cref="BackgroundService"/> drains the queue, batches, and ships to Seq's raw CLEF ingestion endpoint.
/// A Seq outage never loses events silently: a failed batch retries with backoff, and if the retry budget
/// is exhausted the batch is logged via <see cref="ILogger"/> (still observable locally) before being
/// dropped — the bounded channel itself is the durability ceiling for an in-process buffer; a
/// process-crash-durable queue would need a disk-backed store, which this self-host scaffold does not
/// attempt (see docker-compose.prod.yml's own "ceilings" note).
/// </summary>
public sealed class SecurityTelemetryDispatcher : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly ChannelReader<DenialEvent> _reader;
    private readonly HttpClient _http;
    private readonly string? _ingestionUrl;
    private readonly string _applicationName;
    private readonly ILogger<SecurityTelemetryDispatcher> _logger;

    public SecurityTelemetryDispatcher(
        SecurityTelemetryChannel channel, HttpClient http, string? ingestionUrl, string applicationName,
        ILogger<SecurityTelemetryDispatcher> logger)
    {
        _reader = channel.Reader;
        _http = http;
        _ingestionUrl = ingestionUrl;
        _applicationName = applicationName;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<DenialEvent>(BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            batch.Clear();
            using var window = new CancellationTokenSource(BatchWindow);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, window.Token);
            try
            {
                while (batch.Count < BatchSize)
                    batch.Add(await _reader.ReadAsync(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Batch window elapsed — ship whatever was collected, however small.
            }

            if (batch.Count > 0)
                await ShipAsync(batch, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ShipAsync(List<DenialEvent> batch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ingestionUrl))
        {
            LogLocally(batch);
            return;
        }

        var payload = BuildClefPayload(batch);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/vnd.serilog.clef");
                using var response = await _http.PostAsync(
                    $"{_ingestionUrl.TrimEnd('/')}/api/events/raw", content, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Transient — retry below.
            }

            if (attempt < MaxAttempts)
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        // Retry budget exhausted — never lose the event silently.
        LogLocally(batch);
    }

    private void LogLocally(List<DenialEvent> batch)
    {
        foreach (var evt in batch)
            _logger.LogWarning(
                "SecurityDenial {Category} actor={ActorKind}:{ActorId} target={TargetMerchant} entity={Entity} "
                + "op={Operation} reason={Reason} correlation={CorrelationId}",
                evt.Category, evt.ActorKind, evt.ActorId, evt.TargetMerchant, evt.Entity, evt.Operation,
                evt.Reason, evt.CorrelationId);
    }

    private string BuildClefPayload(List<DenialEvent> batch)
    {
        var sb = new StringBuilder();
        foreach (var evt in batch)
        {
            var clefEvent = new Dictionary<string, object?>
            {
                ["@t"] = evt.OccurredAt.ToString("O"),
                ["@mt"] = "SecurityDenial {Category} actor={ActorKind}:{ActorId} target={TargetMerchant} "
                    + "entity={Entity} op={Operation} reason={Reason}",
                ["@l"] = "Warning",
                ["Category"] = evt.Category.ToString(),
                ["ActorKind"] = evt.ActorKind,
                ["ActorId"] = evt.ActorId,
                ["TargetMerchant"] = evt.TargetMerchant,
                ["Entity"] = evt.Entity,
                ["Operation"] = evt.Operation,
                ["Reason"] = evt.Reason,
                ["CorrelationId"] = evt.CorrelationId,
                ["Application"] = _applicationName,
            };
            sb.Append(JsonSerializer.Serialize(clefEvent));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
