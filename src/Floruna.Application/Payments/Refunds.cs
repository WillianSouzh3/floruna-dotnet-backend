using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Application.Ordering;
using Floruna.Application.Outbox;
using Floruna.Domain.Audit;
using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Floruna.Application.Payments;

public sealed record RefundItemResponse(string ProductId, string Name, int Quantity);

public sealed record RefundResponse(
    string Id, string OrderId, long Amount, string Scope, string Method, string Status, string Reason, IReadOnlyList<RefundItemResponse> Items,
    bool Restock, string? GatewayRefundId, int Attempts, string? LastError, bool NeedsAttention, string CreatedAt, string? CompletedAt)
{
    public static RefundResponse From(Refund r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Id.ToString(), r.OrderId.ToString(), r.AmountCents, r.Scope.ToString().ToLowerInvariant(), r.Method.ToString().ToLowerInvariant(),
            r.Status.ToString().ToLowerInvariant(), r.Reason, [.. r.Items.Select(i => new RefundItemResponse(i.ProductId, i.Name, i.Quantity))],
            r.Restock, r.ProviderRefundId, r.Attempts, r.LastError, r.NeedsAttention, CatalogMapping.Iso(r.CreatedAt),
            r.CompletedAt is { } c ? CatalogMapping.Iso(c) : null);
    }
}

public sealed record OrderRefundEnvelope(OrderResponse Order, RefundResponse Refund);

public interface IRefundQueries
{
    /// <summary>Reembolsos pendentes cuja vez chegou, de TODAS as lojas (job).</summary>
    Task<IReadOnlyList<(Guid TenantId, Guid RefundId)>> DueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}

public sealed record RefundRequest(
    OrderId OrderId, long? AmountCents, RefundScope Scope, string Reason, string IdempotencyKey, IReadOnlyList<ReturnedItem> Items, bool Restock, AuditActor Actor);

/// <summary>
/// A saga do reembolso (ver <see cref="Refund"/>): reserva → gateway → conclusão. O gateway nunca
/// é chamado com transação aberta; a reserva garante que, nesse meio-tempo, ninguém reembolsa
/// além do pago.
/// </summary>
public sealed partial class RefundSaga(
    IRefundRepository refunds,
    IPaymentRepository payments,
    IOrderRepository orders,
    IInventoryRepository inventory,
    IPaymentGateway gateway,
    ISecretProtector secrets,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    Telemetry.FlorunaMetrics metrics,
    TimeProvider clock,
    ILogger<RefundSaga> logger)
{
    public static readonly AuditActor System = new(null, "system", null, string.Empty);

    /// <summary>Passo 1: reserva. Idempotente pela chave (uma tentativa RECUSADA não prende a chave: gera outra).</summary>
    /// <returns>O reembolso e se ele foi criado agora (quem criou tem a vez de executar).</returns>
    public async Task<Result<(Refund Refund, bool Created)>> RequestAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await payments.FindByOrderAsync(request.OrderId, cancellationToken) is not { } found)
        {
            return PaymentErrors.NotRefundable;
        }

        var intentId = found.Id;
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var intent = (await refunds.GetIntentForUpdateAsync(intentId, ct))!;
                var key = request.IdempotencyKey;
                for (var attempt = 1; await refunds.FindByKeyAsync(key, ct) is { } existing; attempt++)
                {
                    if (existing.Status != RefundStatus.Failed)
                    {
                        return Result.Success((existing, false));
                    }

                    key = $"{request.IdempotencyKey}:retry{attempt}";
                }

                var amount = request.Scope == RefundScope.Full ? intent.RefundableCents : request.AmountCents ?? 0;
                var reserved = intent.ReserveRefund(amount);
                if (reserved.IsFailure)
                {
                    return Result.Failure<(Refund, bool)>(reserved.Error);
                }

                var now = clock.GetUtcNow();
                var method = intent is { Rail: PaymentRail.MercadoPago, ProviderPaymentId: not null } ? RefundMethod.Gateway : RefundMethod.Manual;
                var refund = Refund.Request(intent.TenantId, intent.Id, intent.OrderId, amount, request.Scope, method, request.Reason.Trim(), key,
                    request.Items, request.Restock, request.Actor.UserId?.ToString() ?? request.Actor.Role, now);
                refunds.Add(refund);
                if (request.Scope == RefundScope.Full && await orders.GetAsync(intent.OrderId, ct) is { } order)
                {
                    order.RefundChanged("processing", intent.RefundedAmountCents, false, now);
                }

                await unitOfWork.SaveChangesAsync(ct);
                return Result.Success((refund, true));
            }, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return await refunds.FindByKeyAsync(request.IdempotencyKey, cancellationToken) is { } raced ? (raced, false) : PaymentErrors.RefundNotFound;
        }
    }

    /// <summary>Passos 2 e 3: chama o gateway (fora de transação) e conclui.</summary>
    /// <param name="alreadyClaimed">Quem acabou de criar já tem a vez (lease da criação); o job precisa reivindicar.</param>
    public async Task<Result<Refund>> ExecuteAsync(Guid refundId, bool alreadyClaimed, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!alreadyClaimed && !await refunds.TryClaimAsync(refundId, clock.GetUtcNow(), cancellationToken))
        {
            return await refunds.GetAsync(refundId, cancellationToken) is { } busy ? busy : PaymentErrors.RefundNotFound;
        }

        var refund = await refunds.GetAsync(refundId, cancellationToken);
        if (refund is null)
        {
            return PaymentErrors.RefundNotFound;
        }

        if (refund.Status != RefundStatus.Pending)
        {
            return refund;
        }

        var outcome = await CallGatewayAsync(refund, cancellationToken);
        var finished = await FinishAsync(refund.Id, refund.IntentId, outcome, actor, cancellationToken);
        metrics.Refund(outcome.Kind switch { OutcomeKind.Succeeded => "succeeded", OutcomeKind.Rejected => "failed", _ => "deferred" });
        return finished;
    }

    private async Task<Outcome> CallGatewayAsync(Refund refund, CancellationToken cancellationToken)
    {
        if (refund.Method == RefundMethod.Manual)
        {
            return new Outcome(OutcomeKind.Succeeded, null, null); // PIX direto: a loja devolve; o sistema registra
        }

        var intent = await payments.GetAsync(refund.IntentId, cancellationToken);
        var settings = await payments.GetSettingsAsync(cancellationToken);
        var token = settings?.MercadoPagoTokenCiphertext is { } cipher ? secrets.Unprotect(cipher, SecretContexts.MercadoPagoToken(refund.TenantId)) : null;
        if (token is null || intent?.ProviderPaymentId is not { } providerPaymentId)
        {
            // Nada foi enviado ao gateway: recusa definitiva, a reserva volta.
            return new Outcome(OutcomeKind.Rejected, null, "Token do Mercado Pago não está configurado.");
        }

        var result = await gateway.RefundAsync(token, providerPaymentId, refund.AmountCents, $"refund-{refund.Id}", cancellationToken);
        if (result.IsSuccess)
        {
            return new Outcome(OutcomeKind.Succeeded, result.Value.ProviderRefundId, null);
        }

        return result.Error.Code == GatewayErrors.Rejected
            ? new Outcome(OutcomeKind.Rejected, null, result.Error.Message)
            : new Outcome(OutcomeKind.Unknown, null, result.Error.Message);
    }

    private async Task<Result<Refund>> FinishAsync(Guid refundId, Guid intentId, Outcome outcome, AuditActor actor, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Mesma ordem de trava da reserva: intenção, depois reembolso.
            var intent = (await refunds.GetIntentForUpdateAsync(intentId, ct))!;
            var refund = (await refunds.GetForUpdateAsync(refundId, ct))!;
            var order = await orders.GetAsync(refund.OrderId, ct);
            var now = clock.GetUtcNow();
            switch (outcome.Kind)
            {
                case OutcomeKind.Succeeded when refund.Succeed(outcome.ProviderRefundId, now):
                    intent.CompleteRefund(refund.AmountCents);
                    order?.RefundChanged(refund.Scope == RefundScope.Full ? "refunded" : order.RefundStatus, intent.RefundedAmountCents,
                        intent.Status == PaymentIntentStatus.Refunded, now);
                    if (refund.Restock)
                    {
                        await RestockAsync(refund, now, ct);
                    }

                    Audit(refund, actor, refund.Scope == RefundScope.Full ? "order.refund_succeeded" : "order.refund_partial",
                        $"Reembolso {(refund.Method == RefundMethod.Gateway ? "via gateway" : "manual")} de {refund.AmountCents} centavos ({refund.Reason}).", now);
                    break;
                case OutcomeKind.Rejected when refund.Fail(outcome.Error!, now):
                    intent.ReleaseRefund(refund.AmountCents);
                    if (refund.Scope == RefundScope.Full)
                    {
                        order?.RefundChanged("failed", intent.RefundedAmountCents, false, now);
                    }

                    Audit(refund, actor, "order.refund_failed", outcome.Error!, now);
                    break;
                case OutcomeKind.Unknown when refund.Status == RefundStatus.Pending:
                    refund.Defer(outcome.Error!, now);
                    LogDeferred(logger, refund.Id, refund.Attempts, outcome.Error!);
                    if (refund.NeedsAttention)
                    {
                        intent.FlagReconciliationIssue("refund_unconfirmed");
                    }

                    break;
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(refund);
        }, cancellationToken);

    /// <summary>Itens devolvidos voltam ao estoque com o custo congelado na venda (ordem crescente de produto, como sempre).</summary>
    private async Task RestockAsync(Refund refund, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (item, index) in refund.Items.Select((item, index) => (item, index)).OrderBy(x => x.item.ProductId, StringComparer.Ordinal))
        {
            var productId = new ProductId(Guid.Parse(item.ProductId));
            if (await inventory.GetForUpdateAsync(productId, ct) is not { } stock)
            {
                continue;
            }

            var change = stock.Receive(item.Quantity, null, item.UnitCostCents, "Devolução", "reembolso", now, StockMovementType.Devolucao);
            inventory.Add(StockMovement.Record(refund.TenantId, productId, item.Name, change, $"Devolução do pedido {refund.OrderId}", "reembolso", now,
                refund.OrderId.Value, $"refund:{refund.Id}:{index}"));
        }
    }

    private void Audit(Refund refund, AuditActor actor, string action, string summary, DateTimeOffset now) =>
        audit.Add(AuditEntry.Record(refund.TenantId, action, actor, "order", refund.OrderId.ToString(), summary.Length > 300 ? summary[..300] : summary, now));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reembolso {RefundId} sem resposta definitiva (tentativa {Attempt}): {Reason}. Fica pendente para nova tentativa.")]
    private static partial void LogDeferred(ILogger logger, Guid refundId, int attempt, string reason);

    private enum OutcomeKind
    {
        Succeeded,
        Rejected,
        Unknown,
    }

    private sealed record Outcome(OutcomeKind Kind, string? ProviderRefundId, string? Error);
}

/// <summary>
/// Cancelar um pedido PAGO = devolver o dinheiro ANTES de cancelar. Se o reembolso não for
/// confirmado, o pedido NÃO é cancelado (o cliente não fica sem a flor e sem o dinheiro).
/// </summary>
public sealed class PaidOrderCancellation(IPaymentRepository payments, IOrderRepository orders, RefundSaga saga)
{
    public async Task<Result> EnsureRefundedAsync(OrderId orderId, OrderStatus? expected, string? reason, AuditActor actor, CancellationToken cancellationToken)
    {
        var intent = await payments.FindByOrderAsync(orderId, cancellationToken);
        if (intent is not { Status: PaymentIntentStatus.Paid })
        {
            return Result.Success(); // não pago (ou já reembolsado): segue o cancelamento normal
        }

        // Pré-checagens ANTES de devolver dinheiro: não reembolsa um pedido que não poderia ser cancelado.
        var order = await orders.GetAsync(orderId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        if (expected is { } e && e != order.Status)
        {
            return OrderErrors.UnexpectedState(order.Status);
        }

        if (!Order.CanMove(order.Status, OrderStatus.Cancelado))
        {
            return OrderErrors.InvalidTransition(order.Status, OrderStatus.Cancelado);
        }

        var requested = await saga.RequestAsync(new RefundRequest(orderId, null, RefundScope.Full, reason ?? "Cancelamento do pedido",
            $"cancel:{orderId}", [], false, actor), cancellationToken);
        if (requested.IsFailure)
        {
            // Saldo todo comprometido por um reembolso parcial ainda sem resposta: espere ele terminar.
            return intent.RefundReservedCents > 0 ? RefundErrors.InProgress : requested.Error;
        }

        var refund = await saga.ExecuteAsync(requested.Value.Refund.Id, requested.Value.Created, actor, cancellationToken);
        if (refund.IsFailure)
        {
            return refund.Error;
        }

        return refund.Value.Status switch
        {
            RefundStatus.Succeeded => Result.Success(),
            RefundStatus.Failed => RefundErrors.NotConfirmed(refund.Value.LastError),
            _ => RefundErrors.InProgress,
        };
    }
}

public static class RefundErrors
{
    public static readonly Error InProgress = Error.Conflict("CONFLICT",
        "O reembolso deste pedido está em processamento. O pedido será cancelado assim que o gateway confirmar.");

    public static Error NotConfirmed(string? reason) =>
        Error.Upstream("REFUND_NOT_CONFIRMED", $"Reembolso não confirmado. O pedido não foi cancelado: {reason}");
}

/// <summary>
/// Reembolso integral confirmado DEPOIS (pelo job, após um timeout): agora sim cancela o pedido.
/// Se o pedido não puder mais ser cancelado, o dinheiro já voltou: marca para reconciliação.
/// </summary>
internal sealed class RefundSucceededHandler(IOrderRepository orders, IPaymentRepository payments, ISender sender, IUnitOfWork unitOfWork)
    : INotificationHandler<DomainEventNotification<RefundSucceeded>>
{
    public async Task Handle(DomainEventNotification<RefundSucceeded> notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var e = notification.Event;
        if (e.Scope != RefundScope.Full || await orders.GetAsync(e.OrderId, cancellationToken) is not { } order || order.Status == OrderStatus.Cancelado)
        {
            return;
        }

        var cancelled = await sender.Send(new ChangeOrderStatusCommand(
            e.OrderId.Value, "cancelado", OrderStatusContract.ToContract(order.Status), $"refund:{e.RefundId}", "Reembolso integral confirmado", null, "system"),
            cancellationToken);
        if (cancelled.IsFailure && await payments.FindByOrderAsync(e.OrderId, cancellationToken) is { } intent)
        {
            intent.FlagReconciliationIssue("refund_completed_order_not_cancelled");
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}

// ── Reembolso parcial pós-venda (POST /orders/{id}/resolutions/refund) ──

public sealed record RefundItemInput(string? ProductId, int Quantity);

public sealed record PartialRefundCommand(
    Guid OrderId, long Amount, string? Reason, IReadOnlyList<RefundItemInput>? Items, bool Restock, string? IdempotencyKey, AuditActor Actor)
    : ICommand<OrderRefundEnvelope>;

internal sealed class PartialRefundValidator : AbstractValidator<PartialRefundCommand>
{
    public PartialRefundValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("Informe um valor de reembolso válido.");
        RuleFor(x => x.Reason).NotEmpty().Length(3, 500).WithMessage("Informe o motivo (3 a 500 caracteres).");
        RuleFor(x => x.Items).Must(i => i is null || i.Count <= 100).WithMessage("No máximo 100 itens.");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.ProductId).NotEmpty();
            i.RuleFor(x => x.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.IdempotencyKey).MaximumLength(120);
    }
}

internal sealed class PartialRefundHandler(IOrderRepository orders, IPaymentRepository payments, IRefundRepository refunds, RefundSaga saga)
    : ICommandHandler<PartialRefundCommand, OrderRefundEnvelope>
{
    public async Task<Result<OrderRefundEnvelope>> Handle(PartialRefundCommand request, CancellationToken cancellationToken)
    {
        var orderId = new OrderId(request.OrderId);
        if (await orders.GetAsync(orderId, cancellationToken) is not { } order)
        {
            return OrderErrors.NotFound;
        }

        var returned = await ResolveItemsAsync(order, request.Items ?? [], cancellationToken);
        if (returned.IsFailure)
        {
            return returned.Error;
        }

        // Sem chave, cada clique é um reembolso novo (como no Nest); com chave, repetir devolve o mesmo.
        var key = $"partial:{order.Id}:{request.IdempotencyKey ?? Guid.NewGuid().ToString()}";
        var requested = await saga.RequestAsync(new RefundRequest(orderId, request.Amount, RefundScope.Partial, request.Reason!, key,
            returned.Value, request.Restock && returned.Value.Count > 0, request.Actor), cancellationToken);
        if (requested.IsFailure)
        {
            return requested.Error;
        }

        var refund = await saga.ExecuteAsync(requested.Value.Refund.Id, requested.Value.Created, request.Actor, cancellationToken);
        if (refund.IsFailure)
        {
            return refund.Error;
        }

        if (refund.Value.Status == RefundStatus.Failed)
        {
            return Error.Upstream("REFUND_NOT_CONFIRMED", $"Reembolso não confirmado: {refund.Value.LastError}");
        }

        var fresh = await orders.GetAsync(orderId, cancellationToken);
        return new OrderRefundEnvelope(OrderResponse.From(fresh!, internalView: true), RefundResponse.From(refund.Value));
    }

    /// <summary>Itens devolvidos precisam existir no pedido, sem passar do vendido menos o já devolvido.</summary>
    private async Task<Result<List<ReturnedItem>>> ResolveItemsAsync(Order order, IReadOnlyList<RefundItemInput> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new List<ReturnedItem>(); // reembolso de cortesia, sem devolução física
        }

        var intent = await payments.FindByOrderAsync(order.Id, cancellationToken);
        var previous = intent is null ? [] : await refunds.ByIntentAsync(intent.Id, cancellationToken);
        var alreadyReturned = previous.Where(r => r.Status != RefundStatus.Failed).SelectMany(r => r.Items)
            .GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        var result = new List<ReturnedItem>();
        foreach (var group in items.GroupBy(i => i.ProductId!))
        {
            var lines = order.Items.Where(i => i.ProductId.ToString() == group.Key).ToList();
            var sold = lines.Sum(i => i.Quantity) - alreadyReturned.GetValueOrDefault(group.Key);
            var quantity = group.Sum(i => i.Quantity);
            if (lines.Count == 0 || quantity > sold)
            {
                return Error.Validation("VALIDATION_ERROR", $"Item devolvido inválido: {group.Key} (quantidade acima da vendida ou item fora do pedido).");
            }

            result.Add(new ReturnedItem(group.Key, lines[0].Name, quantity, lines[0].UnitCostCents));
        }

        return result;
    }
}

// ── Retentativa (job) ──

public sealed record RetryRefundCommand(Guid RefundId) : ICommand<RefundResponse>;

internal sealed class RetryRefundHandler(RefundSaga saga) : ICommandHandler<RetryRefundCommand, RefundResponse>
{
    public async Task<Result<RefundResponse>> Handle(RetryRefundCommand request, CancellationToken cancellationToken)
    {
        var refund = await saga.ExecuteAsync(request.RefundId, alreadyClaimed: false, RefundSaga.System, cancellationToken);
        return refund.IsFailure ? refund.Error : RefundResponse.From(refund.Value);
    }
}
