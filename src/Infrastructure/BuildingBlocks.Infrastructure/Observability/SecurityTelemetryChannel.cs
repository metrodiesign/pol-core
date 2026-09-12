using System.Threading.Channels;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure.Observability;

/// <summary>
/// The <see cref="ISecurityTelemetry"/> implementation every write/read path actually depends on
/// (REQ-13.4's non-blocking half): <see cref="Emit"/> is a bounded, non-blocking channel write — it never
/// awaits, never throws, and never touches the network. <see cref="SecurityTelemetryDispatcher"/> drains
/// the other end and owns delivery to Seq. Bounded at 10,000 (denial events are rare relative to normal
/// traffic; a channel this deep only fills under a sustained attack/bug, and DropOldest keeps the newest
/// signal rather than stalling the app on a full buffer).
/// </summary>
public sealed class SecurityTelemetryChannel : ISecurityTelemetry
{
    private readonly Channel<DenialEvent> _channel = Channel.CreateBounded<DenialEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly ILogger<SecurityTelemetryChannel> _logger;

    public SecurityTelemetryChannel(ILogger<SecurityTelemetryChannel> logger) => _logger = logger;

    internal ChannelReader<DenialEvent> Reader => _channel.Reader;

    public void Emit(DenialEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
            _logger.LogWarning(
                "SecurityDenial channel full — dropped {Category} for entity={Entity}", evt.Category, evt.Entity);
    }
}
