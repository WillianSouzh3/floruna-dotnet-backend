using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Inventory;

/// <summary>Reposição (<c>POST /stock/:productId/restock</c>): cria um lote; o custo informado vira o custo do produto.</summary>
public sealed record RestockCommand(Guid ProductId, int Quantity, DateTimeOffset? ExpiresAt, long? CostCents, string? Note, string By)
    : ICommand<StockOperationResponse>;

/// <summary>Ajuste de inventário ± (<c>POST /stock/:productId/adjust</c>).</summary>
public sealed record AdjustStockCommand(Guid ProductId, int Delta, string? Note, string By) : ICommand<StockOperationResponse>;

/// <summary>Perda/descarte (<c>POST /stock/:productId/loss</c>).</summary>
public sealed record RegisterLossCommand(Guid ProductId, int Quantity, string? Note, string By) : ICommand<StockOperationResponse>;

internal sealed class RestockValidator : AbstractValidator<RestockCommand>
{
    public RestockValidator()
    {
        RuleFor(x => x.Quantity).GreaterThanOrEqualTo(1).WithMessage("A quantidade deve ser de ao menos 1.");
        RuleFor(x => x.CostCents).GreaterThanOrEqualTo(0).When(x => x.CostCents is not null).WithMessage("O custo não pode ser negativo.");
        RuleFor(x => x.Note).MaximumLength(200).WithMessage("A observação deve ter até 200 caracteres.");
    }
}

internal sealed class AdjustStockValidator : AbstractValidator<AdjustStockCommand>
{
    public AdjustStockValidator()
    {
        RuleFor(x => x.Delta).NotEqual(0).WithMessage("O ajuste não pode ser zero.");
        RuleFor(x => x.Note).MaximumLength(200).WithMessage("A observação deve ter até 200 caracteres.");
    }
}

internal sealed class RegisterLossValidator : AbstractValidator<RegisterLossCommand>
{
    public RegisterLossValidator()
    {
        RuleFor(x => x.Quantity).GreaterThanOrEqualTo(1).WithMessage("A quantidade deve ser de ao menos 1.");
        RuleFor(x => x.Note).MaximumLength(200).WithMessage("A observação deve ter até 200 caracteres.");
    }
}

/// <summary>
/// Os três fluxos têm o mesmo esqueleto (é o padrão Template Method, aqui como
/// método genérico): abre transação → trava o estoque do produto → aplica a regra
/// de domínio → grava o movimento no ledger → confirma. Só o passo do meio muda.
/// </summary>
internal sealed class StockCommandHandlers(
    ITenantContext tenantContext,
    IInventoryRepository inventory,
    ICatalogRepository catalog,
    ICatalogQueries catalogQueries,
    IInventoryQueries inventoryQueries,
    ICatalogCache cache,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<RestockCommand, StockOperationResponse>,
      ICommandHandler<AdjustStockCommand, StockOperationResponse>,
      ICommandHandler<RegisterLossCommand, StockOperationResponse>
{
    public Task<Result<StockOperationResponse>> Handle(RestockCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request.ProductId, request.Note, request.By, (item, product, now) =>
        {
            if (request.CostCents is { } cost)
            {
                product.UpdateCost(cost, now);
            }

            return item.Receive(request.Quantity, request.ExpiresAt, request.CostCents, request.Note ?? string.Empty, request.By, now);
        }, cancellationToken);

    public Task<Result<StockOperationResponse>> Handle(AdjustStockCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request.ProductId, request.Note, request.By, (item, _, now) => item.Adjust(request.Delta, request.Note ?? string.Empty, request.By, now), cancellationToken);

    public Task<Result<StockOperationResponse>> Handle(RegisterLossCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request.ProductId, request.Note, request.By, (item, _, now) => item.RegisterLoss(request.Quantity, now), cancellationToken);

    private async Task<Result<StockOperationResponse>> ExecuteAsync(
        Guid productId,
        string? note,
        string by,
        Func<StockItem, Product, DateTimeOffset, Result<StockChange>> operation,
        CancellationToken cancellationToken)
    {
        var id = new ProductId(productId);
        var outcome = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var product = await catalog.GetProductAsync(id, ct);
            if (product is null)
            {
                return Result.Failure<Written>(CatalogErrors.ProductNotFound);
            }

            // Trava a linha do estoque deste produto até o commit: operações
            // simultâneas no mesmo produto acontecem em fila, nunca sobrepostas.
            var item = await inventory.GetForUpdateAsync(id, ct);
            if (item is null)
            {
                item = StockItem.Open(id, tenantContext.RequiredTenantId);
                inventory.Add(item);
            }

            var now = clock.GetUtcNow();
            var change = operation(item, product, now);
            if (change.IsFailure)
            {
                return Result.Failure<Written>(change.Error);
            }

            var movement = StockMovement.Record(tenantContext.RequiredTenantId, id, product.Name, change.Value, note ?? string.Empty, by, now);
            inventory.Add(movement);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(new Written(product.Slug.Value, movement.Id));
        }, cancellationToken);

        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        await cache.InvalidateAsync(cancellationToken); // a disponibilidade mudou: a vitrine precisa ver

        var product = await catalogQueries.GetProductBySlugAsync(outcome.Value.ProductSlug, includeCost: true, cancellationToken);
        var movement = (await inventoryQueries.MovementsAsync(productId, cancellationToken))
            .First(m => m.Id == outcome.Value.MovementId.ToString());
        return new StockOperationResponse(product!, movement);
    }

    /// <summary>O que a transação gravou, para montar a resposta depois do commit.</summary>
    private sealed record Written(string ProductSlug, Guid MovementId);
}
