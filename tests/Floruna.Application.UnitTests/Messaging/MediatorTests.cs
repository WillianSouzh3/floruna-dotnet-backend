using Floruna.Application.Messaging;
using Floruna.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Application.UnitTests.Messaging;

public sealed class MediatorTests
{
    // ── Mensagens e handlers de teste ─────────────────────────────────────

    public sealed record Ping(string Message) : IQuery<string>;

    internal sealed class PingHandler(Trace trace) : IQueryHandler<Ping, string>
    {
        public Task<Result<string>> Handle(Ping request, CancellationToken cancellationToken)
        {
            trace.Steps.Add("handler");
            return Task.FromResult<Result<string>>($"pong: {request.Message}");
        }
    }

    public sealed record Unhandled : IQuery<int>;

    public sealed record SomethingHappened(int Value) : INotification;

    internal sealed class FirstListener(Trace trace) : INotificationHandler<SomethingHappened>
    {
        public Task Handle(SomethingHappened notification, CancellationToken cancellationToken)
        {
            trace.Steps.Add($"first:{notification.Value}");
            return Task.CompletedTask;
        }
    }

    internal sealed class SecondListener(Trace trace) : INotificationHandler<SomethingHappened>
    {
        public Task Handle(SomethingHappened notification, CancellationToken cancellationToken)
        {
            trace.Steps.Add($"second:{notification.Value}");
            return Task.CompletedTask;
        }
    }

    internal sealed class OuterBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
        {
            trace.Steps.Add("outer:before");
            var response = await next();
            trace.Steps.Add("outer:after");
            return response;
        }
    }

    internal sealed class InnerBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
        {
            trace.Steps.Add("inner:before");
            var response = await next();
            trace.Steps.Add("inner:after");
            return response;
        }
    }

    internal sealed class ShortCircuitBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken) =>
            Task.FromResult((TResponse)(object)Result.Failure<string>(Error.Forbidden("BLOCKED", "Bloqueado.")));
    }

    internal sealed class Trace
    {
        public List<string> Steps { get; } = [];
    }

    private static ServiceProvider Build(params Type[] behaviors)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Trace>();
        services.AddMessaging(typeof(MediatorTests).Assembly);
        foreach (var behavior in behaviors)
        {
            services.AddPipelineBehavior(behavior);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // ── Testes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_routes_the_request_to_its_handler()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping("oi"));

        result.Value.ShouldBe("pong: oi");
    }

    [Fact]
    public async Task Behaviors_wrap_the_handler_in_registration_order()
    {
        await using var provider = Build(typeof(OuterBehavior<,>), typeof(InnerBehavior<,>));
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping("x"));

        provider.GetRequiredService<Trace>().Steps.ShouldBe(
            ["outer:before", "inner:before", "handler", "inner:after", "outer:after"]);
    }

    [Fact]
    public async Task A_behavior_can_short_circuit_without_calling_the_handler()
    {
        await using var provider = Build(typeof(ShortCircuitBehavior<,>));
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping("x"));

        result.Error.Code.ShouldBe("BLOCKED");
        provider.GetRequiredService<Trace>().Steps.ShouldNotContain("handler");
    }

    [Fact]
    public async Task Send_without_registered_handler_throws_a_descriptive_error()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Unhandled()));

        exception.Message.ShouldContain(nameof(Unhandled));
    }

    [Fact]
    public async Task Publish_delivers_to_every_handler_in_sequence()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new SomethingHappened(7));

        provider.GetRequiredService<Trace>().Steps.ShouldBe(["first:7", "second:7"]);
    }

    [Fact]
    public async Task Sender_and_publisher_are_the_same_scoped_instance()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        object sender = scope.ServiceProvider.GetRequiredService<ISender>();
        object publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();

        sender.ShouldBeSameAs(publisher);
    }

    [Fact]
    public void AddPipelineBehavior_rejects_closed_generic_types()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentException>(
            () => services.AddPipelineBehavior(typeof(OuterBehavior<Ping, Result<string>>)));
    }
}
