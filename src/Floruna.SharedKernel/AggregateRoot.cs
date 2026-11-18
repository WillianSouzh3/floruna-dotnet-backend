namespace Floruna.SharedKernel;

/// <summary>
/// Fato relevante para o negócio que JÁ aconteceu (nome no passado:
/// <c>OrderPlaced</c>, <c>PaymentConfirmed</c>).
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>Base para eventos de domínio imutáveis.</summary>
public abstract record DomainEvent(DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>Visão não genérica, usada pela infraestrutura para recolher eventos de qualquer agregado.</summary>
public interface IAggregateRoot
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

/// <summary>
/// Raiz de agregado: a única porta de entrada para alterar o agregado e a
/// fronteira das suas invariantes. Uma transação altera UM agregado.
/// </summary>
/// <remarks>
/// O agregado não publica eventos; ele os REGISTRA. A infraestrutura os
/// recolhe no SaveChanges e grava na outbox, na mesma transação. Assim o
/// evento só "existe" se o estado foi persistido.
/// </remarks>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
