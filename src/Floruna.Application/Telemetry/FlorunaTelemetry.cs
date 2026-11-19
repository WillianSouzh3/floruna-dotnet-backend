using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Floruna.Application.Telemetry;

/// <summary>
/// Nomes da telemetria do Floruna. Usa só o BCL (<see cref="ActivitySource"/>, <see cref="Meter"/>):
/// as camadas internas não dependem do OpenTelemetry; quem exporta (OTLP, X-Ray) é decisão da borda.
/// </summary>
public static class FlorunaTelemetry
{
    public const string Name = "Floruna";

    /// <summary>Spans de negócio: um por comando/consulta do mediator e um por mensagem da outbox.</summary>
    public static readonly ActivitySource Source = new(Name);
}

/// <summary>Estado amostrado da outbox (o processador atualiza; os gauges só leem: callback de gauge não faz I/O).</summary>
public sealed class OutboxHealth
{
    public double OldestPendingAgeSeconds { get; set; }

    public long DeadLetters { get; set; }

    public long Pending { get; set; }
}

/// <summary>
/// Métricas de NEGÓCIO (as técnicas, como latência HTTP e pool do banco, vêm das instrumentações).
/// Valores monetários em centavos, como no resto do sistema. Sem tag de alta cardinalidade (nada de id
/// de pedido ou de loja): métrica é para tendência; para o caso específico existe o trace.
/// </summary>
public sealed class FlorunaMetrics
{
    private readonly Counter<long> _ordersPlaced;
    private readonly Counter<long> _orderValue;
    private readonly Counter<long> _paymentsConfirmed;
    private readonly Counter<long> _refunds;
    private readonly Counter<long> _outboxProcessed;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _requestDuration;

    public FlorunaMetrics(IMeterFactory meterFactory, OutboxHealth outbox)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(outbox);
        var meter = meterFactory.Create(FlorunaTelemetry.Name);
        _ordersPlaced = meter.CreateCounter<long>("floruna.orders.placed", "{order}", "Pedidos criados (origem: online ou pdv)");
        _orderValue = meter.CreateCounter<long>("floruna.orders.value", "{cent}", "Valor dos pedidos criados, em centavos");
        _paymentsConfirmed = meter.CreateCounter<long>("floruna.payments.confirmed", "{payment}", "Pagamentos confirmados (webhook ou manual)");
        _refunds = meter.CreateCounter<long>("floruna.refunds", "{refund}", "Reembolsos por desfecho (succeeded, failed, deferred)");
        _outboxProcessed = meter.CreateCounter<long>("floruna.outbox.messages", "{message}", "Mensagens da outbox por desfecho (processed, retry, dead)");
        _requests = meter.CreateCounter<long>("floruna.requests", "{request}", "Comandos e consultas do mediator por desfecho");
        _requestDuration = meter.CreateHistogram<double>("floruna.request.duration", "ms", "Duração de comandos e consultas do mediator");

        // RNF-04: consistência eventual "< 1 min" só é verificável medindo a idade do que está na fila.
        meter.CreateObservableGauge("floruna.outbox.oldest_pending_age", () => outbox.OldestPendingAgeSeconds, "s", "Idade da mensagem pendente mais antiga");
        meter.CreateObservableGauge("floruna.outbox.pending", () => outbox.Pending, "{message}", "Mensagens aguardando entrega");
        meter.CreateObservableGauge("floruna.outbox.dead_letters", () => outbox.DeadLetters, "{message}", "Mensagens na fila de mortas (precisam de gente)");
    }

    public void OrderPlaced(string origin, long totalCents)
    {
        var tag = new KeyValuePair<string, object?>("origin", origin);
        _ordersPlaced.Add(1, tag);
        _orderValue.Add(totalCents, tag);
    }

    public void PaymentConfirmed(string source) => _paymentsConfirmed.Add(1, new KeyValuePair<string, object?>("source", source));

    public void Refund(string outcome) => _refunds.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void OutboxMessage(string outcome) => _outboxProcessed.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void Request(string name, string outcome, double elapsedMs)
    {
        var tags = new TagList { { "request", name }, { "outcome", outcome } };
        _requests.Add(1, tags);
        _requestDuration.Record(elapsedMs, tags);
    }
}
