using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
