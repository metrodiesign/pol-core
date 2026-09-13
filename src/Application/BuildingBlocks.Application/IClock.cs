namespace BuildingBlocks.Application;

/// <summary>Testable UTC time source. All persisted timestamps are UTC (column suffix <c>Utc</c>).</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
