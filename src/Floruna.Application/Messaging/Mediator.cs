using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Application.Messaging;

/// <summary>
/// Implementação do <see cref="ISender"/> e do <see cref="IPublisher"/>.
/// </summary>
/// <remarks>
/// <para><b>O problema:</b> <c>Send</c> recebe <c>IRequest&lt;TResponse&gt;</c>, mas para
/// resolver o handler no DI precisamos do tipo CONCRETO da request
/// (<c>IRequestHandler&lt;PlaceOrderCommand, Result&lt;OrderId&gt;&gt;</c>), que só se
/// conhece em tempo de execução.</para>
/// <para><b>A solução</b> (a mesma do MediatR): na primeira chamada para um tipo de
/// request, cria-se via reflection um "wrapper" genérico fechado
/// (<c>RequestHandlerWrapper&lt;PlaceOrderCommand, ...&gt;</c>) e guarda-se num cache
/// estático. Daí em diante, a chamada é tipada, sem reflection.</para>
/// <para>Lifetime: scoped. Ele resolve handlers do escopo da requisição (que
/// dependem do DbContext, também scoped).</para>
/// </remarks>
internal sealed class Mediator(IServiceProvider serviceProvider) : ISender, IPublisher
{
    private static readonly ConcurrentDictionary<Type, object> RequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> NotificationWrappers = new();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)RequestWrappers.GetOrAdd(
            request.GetType(),
            static requestType => Activator.CreateInstance(
                typeof(RequestHandlerWrapper<,>).MakeGenericType(requestType, typeof(TResponse)))!);

        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }

    public Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var wrapper = NotificationWrappers.GetOrAdd(
            notification.GetType(),
            static notificationType => (NotificationHandlerWrapper)Activator.CreateInstance(
                typeof(NotificationHandlerWrapper<>).MakeGenericType(notificationType))!);

        return wrapper.Handle(notification, serviceProvider, cancellationToken);
    }
}

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
            ?? throw new InvalidOperationException(
                $"Nenhum handler registrado para {typeof(TRequest).Name}. " +
                "Verifique se a classe implementa IRequestHandler e está no assembly escaneado.");

        // Monta a cadeia de dentro para fora. Os behaviors vêm na ordem de
        // registro; o PRIMEIRO registrado fica mais EXTERNO (executa primeiro).
        //   behaviors = [Logging, Validation]  →  Logging(Validation(handler))
        Func<Task<TResponse>> pipeline = () => handler.Handle(typedRequest, cancellationToken);

        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse();
        foreach (var behavior in behaviors)
        {
            var next = pipeline;
            pipeline = () => behavior.Handle(typedRequest, next, cancellationToken);
        }

        return pipeline();
    }
}

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapper<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    /// <summary>
    /// Executa os handlers em SEQUÊNCIA. Paralelizar dividiria o mesmo
    /// DbContext (scoped, não thread-safe) entre threads. Falha de um handler
    /// interrompe a publicação; a outbox (Fase 4) dará retry e idempotência
    /// por handler.
    /// </summary>
    public override async Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        foreach (var handler in serviceProvider.GetServices<INotificationHandler<TNotification>>())
        {
            await handler.Handle((TNotification)notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
