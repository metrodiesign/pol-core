namespace BuildingBlocks.Application;

/// <summary>The change-tracker state a runtime context's write guard maps a tracked entry to
/// (rls-to-query-filter REQ-2.11) before asking <see cref="IWriteAuthorizer"/> whether it is allowed.</summary>
public enum WriteOperation
{
    Insert,
    Update,
    Delete,
}
