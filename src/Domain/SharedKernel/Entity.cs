namespace SharedKernel;

/// <summary>Marker for an in-aggregate domain event. Cross-module integration events live
/// in <c>Contracts</c> as Mediator notifications; these never cross a module boundary.</summary>
public interface IDomainEvent;

/// <summary>Identity-equal entity base. Two entities of the same runtime type with equal
/// <see cref="Id"/> are equal.</summary>
public abstract class Entity<TId> where TId : notnull
{
    public TId Id { get; protected set; } = default!;

    protected Entity(TId id) => Id = id;

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    protected Entity() { }

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other
        && GetType() == other.GetType()
        && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>Aggregate root: an entity that owns a transactional boundary and records
/// domain events for the infrastructure to translate into the outbox.</summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected AggregateRoot(TId id) : base(id) { }

    protected AggregateRoot() { }
}
