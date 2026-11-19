using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Application.Ordering;
using Floruna.Domain.Audit;
using Floruna.Domain.Inventory;
using Floruna.Domain.Ordering;
using Floruna.Domain.Pos;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Pos;

// ── Venda no balcão (POST /orders/pos) ──

public sealed record PosPaymentInput(string? Method, long Amount);

public sealed record PlacePosSaleCommand(
    IReadOnlyList<OrderItemInput>? Items, IReadOnlyList<PosPaymentInput>? Payments, long? Discount, string? CustomerName, string? CustomerPhone,
    string? IdempotencyKey, AuditActor Operator) : ICommand<OrderEnvelope>;

internal sealed class PlacePosSaleValidator : AbstractValidator<PlacePosSaleCommand>
{
    public PlacePosSaleValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Inclua ao menos um item.");
        RuleFor(x => x.Items!.Count).LessThanOrEqualTo(200).When(x => x.Items is not null);
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.ProductId).NotEmpty();
            i.RuleFor(x => x.Quantity).InclusiveBetween(1, 1_000);
        });
        RuleFor(x => x.Payments).NotEmpty().Must(p => p is null || p.Count <= 5).WithMessage("Informe de 1 a 5 formas de pagamento.");
        RuleForEach(x => x.Payments).ChildRules(p =>
        {
            p.RuleFor(x => x.Method).Must(m => m is not null && PosPayment.Methods.Contains(m)).WithMessage("Forma de pagamento inválida.");
            p.RuleFor(x => x.Amount).GreaterThanOrEqualTo(0);
        });
        RuleFor(x => x.Discount).GreaterThanOrEqualTo(0).When(x => x.Discount is not null);
        RuleFor(x => x.CustomerName).MaximumLength(120);
        RuleFor(x => x.CustomerPhone).MaximumLength(40);
        RuleFor(x => x.IdempotencyKey).MaximumLength(120);
    }
}

/// <summary>
/// Venda de balcão: o mesmo núcleo do checkout (preço do catálogo, trava de estoque em ordem
/// crescente de produto, ledger) numa transação só, sem entrega, cupom nem gateway. A venda
/// entra no caixa aberto do operador; sem caixa aberto, a venda acontece mesmo assim (como no Nest).
/// </summary>
internal sealed class PlacePosSaleHandler(
    ITenantContext tenantContext,
    IOrderRepository orders,
    OrderItemPricer pricer,
    IInventoryRepository inventory,
    ICashSessionRepository cash,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    ICatalogCache cache,
    TimeProvider clock) : ICommandHandler<PlacePosSaleCommand, OrderEnvelope>
{
    public async Task<Result<OrderEnvelope>> Handle(PlacePosSaleCommand request, CancellationToken cancellationToken)
    {
        if (request.IdempotencyKey is { Length: > 0 } key && await orders.FindByIdempotencyKeyAsync(key, cancellationToken) is { } existing)
        {
            return new OrderEnvelope(OrderResponse.From(existing, internalView: true)); // duplo clique no "finalizar venda"
        }

        var items = await pricer.PriceAsync(request.Items!, cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        var operatorId = request.Operator.UserId!.Value;
        Result<Order> written;
        try
        {
            written = await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                // FOR SHARE: o fechamento (FOR UPDATE) espera esta venda; se ele chegou antes, o caixa já aparece fechado.
                var session = await cash.GetOpenForUpdateAsync(operatorId, forSale: true, ct);
                var now = clock.GetUtcNow();
                var placed = Order.PlacePos(OrderId.New(), new PosPlacement(
                    tenantContext.RequiredTenantId, request.IdempotencyKey,
                    new CustomerContact(string.IsNullOrWhiteSpace(request.CustomerName) ? "Consumidor" : request.CustomerName.Trim(),
                        string.IsNullOrWhiteSpace(request.CustomerPhone) ? "Não informado" : request.CustomerPhone.Trim(), null),
                    items.Value, request.Discount ?? 0, [.. request.Payments!.Select(p => new PosPayment(p.Method!, p.Amount))], operatorId, session?.Id), now);
                if (placed.IsFailure)
                {
                    return Result.Failure<Order>(placed.Error);
                }

                var order = placed.Value.Order;
                foreach (var (item, index) in order.Items.Select((item, index) => (item, index)).OrderBy(x => x.item.ProductId.Value))
                {
                    var stock = await inventory.GetForUpdateAsync(item.ProductId, ct);
                    var sale = stock is null ? Result.Failure<StockChange>(OrderErrors.InsufficientStock(item.Name)) : stock.Sell(item.Quantity, now);
                    if (sale.IsFailure)
                    {
                        return Result.Failure<Order>(OrderErrors.InsufficientStock(item.Name));
                    }

                    inventory.Add(StockMovement.Record(order.TenantId, item.ProductId, item.Name, sale.Value, $"Venda no balcão {order.Id}", "pdv", now,
                        order.Id.Value, ReservationKeys.Sale(order.Id, index)));
                }

                orders.Add(order);
                audit.Add(AuditEntry.Record(order.TenantId, "pos.sale", request.Operator, "order", order.Id.ToString(),
                    $"Venda no balcão: {order.Items.Count} item(ns), total {order.TotalCents} centavos ({order.PaymentMethod}).", now));
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Success(order);
            }, cancellationToken);
        }
        catch (UniqueConstraintViolationException) when (request.IdempotencyKey is { Length: > 0 } raced)
        {
            var winner = await orders.FindByIdempotencyKeyAsync(raced, cancellationToken);
            return winner is null ? OrderErrors.NotFound : new OrderEnvelope(OrderResponse.From(winner, internalView: true));
        }

        if (written.IsFailure)
        {
            return written.Error;
        }

        await cache.InvalidateAsync(cancellationToken);
        return new OrderEnvelope(OrderResponse.From(written.Value, internalView: true));
    }
}

// ── Caixa (/cash-sessions) ──

public sealed record CashMovementResponse(string Type, long Amount, string Reason, string At, string By);

public sealed record CashSessionResponse(
    string Id, string OperatorId, string OperatorName, long OpeningFloat, IReadOnlyList<CashMovementResponse> Movements, string Status,
    string OpenedAt, string? ClosedAt, long? CountedCash, long? ExpectedCash, long? Difference, long? CashSales)
{
    /// <param name="cashSales">Com valor: visão "ao vivo" (esperado calculado agora). Sem: registro do relatório.</param>
    public static CashSessionResponse From(CashSession s, long? cashSales)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id.ToString(), s.OperatorId.ToString(), s.OperatorName, s.OpeningFloatCents,
            [.. s.Movements.Select(m => new CashMovementResponse(m.Type.ToString().ToLowerInvariant(), m.AmountCents, m.Reason, CatalogMapping.Iso(m.At), m.By))],
            s.Status.ToString().ToLowerInvariant(), CatalogMapping.Iso(s.OpenedAt), s.ClosedAt is { } c ? CatalogMapping.Iso(c) : null,
            s.CountedCash, cashSales is { } sales && s.Status == CashSessionStatus.Open ? s.ExpectedCash(sales) : s.ExpectedCashAtClose, s.Difference, cashSales);
    }
}

public sealed record CashSessionPage(IReadOnlyList<CashSessionResponse> Items, int Total);

public interface ICashSessionQueries
{
    Task<(IReadOnlyList<CashSessionResponse> Items, int Total)> ClosedAsync(int page, int limit, CancellationToken cancellationToken);
}

public sealed record GetCurrentCashSessionQuery(Guid OperatorId) : IQuery<CashSessionResponse?>;

public sealed record OpenCashSessionCommand(long OpeningFloat, string OperatorName, AuditActor Operator) : ICommand<CashSessionResponse>;

public sealed record AddCashMovementCommand(string? Type, long Amount, string? Reason, string OperatorName, AuditActor Operator) : ICommand<CashSessionResponse>;

public sealed record CloseCashSessionCommand(long CountedCash, AuditActor Operator) : ICommand<CashSessionResponse>;

public sealed record ListClosedCashSessionsQuery(int Page, int Limit) : IQuery<CashSessionPage>;

internal sealed class AddCashMovementValidator : AbstractValidator<AddCashMovementCommand>
{
    public AddCashMovementValidator()
    {
        RuleFor(x => x.Type).Must(t => t is "sangria" or "suprimento").WithMessage("Tipo inválido (sangria ou suprimento).");
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Reason).MaximumLength(200);
    }
}

internal sealed class OpenCashSessionValidator : AbstractValidator<OpenCashSessionCommand>
{
    public OpenCashSessionValidator() => RuleFor(x => x.OpeningFloat).GreaterThanOrEqualTo(0);
}

internal sealed class CloseCashSessionValidator : AbstractValidator<CloseCashSessionCommand>
{
    public CloseCashSessionValidator() => RuleFor(x => x.CountedCash).GreaterThanOrEqualTo(0);
}

internal sealed class CashSessionHandlers(
    ICashSessionRepository cash, ICashSessionQueries queries, ITenantContext tenantContext, IAuditRepository audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : IQueryHandler<GetCurrentCashSessionQuery, CashSessionResponse?>,
      ICommandHandler<OpenCashSessionCommand, CashSessionResponse>,
      ICommandHandler<AddCashMovementCommand, CashSessionResponse>,
      ICommandHandler<CloseCashSessionCommand, CashSessionResponse>,
      IQueryHandler<ListClosedCashSessionsQuery, CashSessionPage>
{
    public async Task<Result<CashSessionResponse?>> Handle(GetCurrentCashSessionQuery request, CancellationToken cancellationToken)
    {
        var session = await cash.FindOpenAsync(request.OperatorId, cancellationToken);
        return session is null ? Result.Success<CashSessionResponse?>(null) : CashSessionResponse.From(session, await cash.CashSalesAsync(session.Id, cancellationToken));
    }

    public async Task<Result<CashSessionResponse>> Handle(OpenCashSessionCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var opened = CashSession.Open(tenantContext.RequiredTenantId, request.Operator.UserId!.Value, request.OperatorName, request.OpeningFloat, now);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        cash.Add(opened.Value);
        audit.Add(AuditEntry.Record(opened.Value.TenantId, "cash.open", request.Operator, "cashSession", opened.Value.Id.ToString(),
            $"Caixa aberto com fundo de {request.OpeningFloat} centavos.", now));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return CashErrors.AlreadyOpen; // dois "abrir caixa" ao mesmo tempo: o índice único parcial decidiu
        }

        return CashSessionResponse.From(opened.Value, 0);
    }

    public async Task<Result<CashSessionResponse>> Handle(AddCashMovementCommand request, CancellationToken cancellationToken) =>
        await InOpenSessionAsync(request.Operator, async (session, sales, now) =>
        {
            var type = request.Type == "sangria" ? CashMovementType.Sangria : CashMovementType.Suprimento;
            var added = session.AddMovement(type, request.Amount, request.Reason ?? string.Empty, request.OperatorName, sales, now);
            if (added.IsSuccess)
            {
                audit.Add(AuditEntry.Record(session.TenantId, "cash.movement", request.Operator, "cashSession", session.Id.ToString(),
                    $"{(type == CashMovementType.Sangria ? "Sangria" : "Suprimento")} de {request.Amount} centavos.", now));
            }

            return added;
        }, cancellationToken);

    public async Task<Result<CashSessionResponse>> Handle(CloseCashSessionCommand request, CancellationToken cancellationToken) =>
        await InOpenSessionAsync(request.Operator, (session, sales, now) =>
        {
            var closed = session.Close(request.CountedCash, sales, now);
            if (closed.IsSuccess)
            {
                audit.Add(AuditEntry.Record(session.TenantId, "cash.close", request.Operator, "cashSession", session.Id.ToString(),
                    $"Caixa fechado: esperado {session.ExpectedCashAtClose}, contado {session.CountedCash}, diferença {session.Difference} centavos.", now));
            }

            return Task.FromResult(closed);
        }, cancellationToken);

    public async Task<Result<CashSessionPage>> Handle(ListClosedCashSessionsQuery request, CancellationToken cancellationToken)
    {
        var (items, total) = await queries.ClosedAsync(request.Page, request.Limit, cancellationToken);
        return new CashSessionPage(items, total);
    }

    /// <summary>
    /// Trava o caixa (FOR UPDATE) e SÓ ENTÃO soma as vendas em dinheiro: vendas em andamento
    /// (que seguram FOR SHARE) terminam antes, então nenhuma venda fica fora da conferência.
    /// </summary>
    private async Task<Result<CashSessionResponse>> InOpenSessionAsync(
        AuditActor actor, Func<CashSession, long, DateTimeOffset, Task<Result>> change, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var session = await cash.GetOpenForUpdateAsync(actor.UserId!.Value, forSale: false, ct);
            if (session is null)
            {
                return Result.Failure<CashSessionResponse>(CashErrors.NoOpenSession);
            }

            var sales = await cash.CashSalesAsync(session.Id, ct);
            var changed = await change(session, sales, clock.GetUtcNow());
            if (changed.IsFailure)
            {
                return Result.Failure<CashSessionResponse>(changed.Error);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(CashSessionResponse.From(session, sales));
        }, cancellationToken);
}
