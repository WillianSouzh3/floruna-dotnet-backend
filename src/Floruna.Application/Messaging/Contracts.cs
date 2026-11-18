using Floruna.SharedKernel;

namespace Floruna.Application.Messaging;

// ─────────────────────────────────────────────────────────────────────────────
// Mediator próprio (padrão GoF Mediator + CQRS).
//
// Ideia: o controller não conhece o handler. Ele entrega uma MENSAGEM
// (comando ou query) ao ISender, que encontra o handler certo e passa a
// mensagem por um pipeline de comportamentos (log, validação, transação...).
//
//   Controller ──Send(cmd)──▶ Sender ──▶ [Logging ▶ Validation ▶ ...] ──▶ Handler
//
// Ganhos: controllers enxutos, handlers com uma responsabilidade (SRP) e
// regras transversais adicionadas sem tocar em nenhum handler (OCP).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Mensagem que produz uma resposta. Marcador sem membros (o tipo é o contrato).</summary>
#pragma warning disable CA1040 // Interface marcadora é intencional: o tipo genérico carrega o contrato.
public interface IRequest<out TResponse>;

/// <summary>Notificação com zero ou mais handlers (eventos de domínio, a partir da Fase 4).</summary>
public interface INotification;
#pragma warning restore CA1040

/// <summary>
/// COMANDO (CQRS): intenção de MUDAR estado. Passa pelo domínio e pela
/// transação. Nome no imperativo: <c>PlaceOrder</c>, <c>ReceiveStockLot</c>.
/// </summary>
public interface ICommand : IRequest<Result>;

/// <summary>Comando que devolve um valor (ex.: o ID criado).</summary>
public interface ICommand<TResponse> : IRequest<Result<TResponse>>;

/// <summary>
/// QUERY (CQRS): só LÊ. Não altera estado, não abre transação, pode ler de
/// projeções otimizadas em vez do modelo de domínio.
/// </summary>
public interface IQuery<TResponse> : IRequest<Result<TResponse>>;

/// <summary>Executa uma mensagem. Existe exatamente UM handler por tipo de request.</summary>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Result>
    where TCommand : ICommand;

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, Result<TResponse>>
    where TCommand : ICommand<TResponse>;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, Result<TResponse>>
    where TQuery : IQuery<TResponse>;

/// <summary>Reage a uma notificação. Vários handlers por notificação são permitidos.</summary>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Etapa do pipeline (padrão Decorator / Chain of Responsibility). Recebe a
/// request e o próximo passo; decide se chama <c>next</c>, o que
/// faz antes e o que faz depois.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken);
}

/// <summary>Porta de entrada usada pelos controllers.</summary>
public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

/// <summary>Publica notificações para todos os handlers registrados.</summary>
public interface IPublisher
{
    Task Publish(INotification notification, CancellationToken cancellationToken = default);
}
