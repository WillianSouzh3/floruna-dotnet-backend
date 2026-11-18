using Floruna.Application.Messaging;
using Floruna.SharedKernel;

namespace Floruna.Application.Outbox;

/// <summary>
/// Envelope que leva um evento de DOMÍNIO para o mediator. O domínio não conhece
/// <see cref="INotification"/> (é um detalhe da aplicação); a outbox embrulha o
/// evento aqui antes de entregar aos handlers.
/// </summary>
public sealed record DomainEventNotification<TEvent>(TEvent Event, Guid MessageId) : INotification
    where TEvent : IDomainEvent;
