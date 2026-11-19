using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Application.Ordering;
using Floruna.Domain.Audit;
using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Floruna.Domain.Ordering;
using Floruna.Domain.Substitutions;
using Floruna.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Floruna.Application.Substitutions;

// ── Contrato ──

public sealed record SubstitutionPolicyResponse(string? Id, int Version, string DefaultMode, int PreapprovedCostTolerancePercent, int ConsentWindowHours, bool IsActive)
{
    public static SubstitutionPolicyResponse From(SubstitutionPolicy p, bool persisted) =>
        new(persisted ? p.Id.ToString() : null, p.Version, Mode(p.DefaultMode), p.PreapprovedTolerancePercent, p.ConsentWindowHours, true);

    internal static string Mode(SubstitutionMode mode) => mode switch
    {
        SubstitutionMode.AskMe => "ask_me",
        var m => m.ToString().ToLowerInvariant(),
    };
}

public sealed record SubstitutionPolicyEnvelope(SubstitutionPolicyResponse SubstitutionPolicy);

/// <remarks>O hash do token nunca sai da API.</remarks>
public sealed record SubstitutionProposalResponse(
    string Id, string OrderId, int OrderItemIndex, string OriginalProductId, string OriginalName, string SubstituteProductId, string SubstituteName,
    int Quantity, long ValueDelta, decimal ValueDeltaPercent, string OrderMode, int PolicyVersion, string State, string? ExpiresAt, string? DecidedAt,
    string DecidedBy, string Reason, bool RestockOriginal, string CreatedAt, string? AppliedAt)
{
    public static SubstitutionProposalResponse From(SubstitutionProposal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Id.ToString(), p.OrderId.ToString(), p.ItemIndex, p.OriginalProductId.ToString(), p.OriginalName, p.SubstituteProductId.ToString(),
            p.SubstituteName, p.Quantity, p.ValueDelta, p.ValueDeltaPercent, SubstitutionPolicyResponse.Mode(p.OrderMode), p.PolicyVersion,
            p.State.ToString().ToLowerInvariant(), Iso(p.ExpiresAt), Iso(p.DecidedAt), p.DecidedBy, p.Reason, p.RestockOriginal,
            CatalogMapping.Iso(p.CreatedAt), Iso(p.AppliedAt));
    }

    private static string? Iso(DateTimeOffset? v) => v is { } value ? CatalogMapping.Iso(value) : null;
}

public sealed record SubstitutionProposalEnvelope(SubstitutionProposalResponse SubstitutionProposal);

public sealed record SubstitutionProposalList(IReadOnlyList<SubstitutionProposalResponse> SubstitutionProposals);

/// <param name="ConsentToken">Só quando a troca precisa da resposta do cliente (para montar o link); aparece uma vez.</param>
public sealed record SubstitutionOutcome(
    OrderResponse Order, SubstitutionProposalResponse SubstitutionProposal,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ConsentToken)
{
    public bool Applied => SubstitutionProposal.State == "applied";
}

public interface ISubstitutionQueries
{
    /// <summary>Propostas sem resposta e vencidas, de TODAS as lojas (job).</summary>
    Task<IReadOnlyList<(Guid TenantId, Guid ProposalId)>> DueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}

// ── Aplicação da troca (porta única) ──

/// <summary>
/// Aplica uma proposta APROVADA: trava a proposta, baixa o substituto (e devolve o original, se pedido),
/// registra no pedido. Tudo numa transação; a proposta só sai de "aprovada" se o estoque permitir.
/// </summary>
public sealed class SubstitutionApplier(ISubstitutionRepository substitutions, IOrderRepository orders, IInventoryRepository inventory, TimeProvider clock)
{
    /// <summary>Chamar DENTRO de uma transação.</summary>
    public async Task<Result<Order>> ApplyAsync(Guid proposalId, CancellationToken ct)
    {
        var proposal = await substitutions.GetForUpdateAsync(proposalId, ct);
        if (proposal is null)
        {
            return SubstitutionErrors.NotFound;
        }

        var order = await orders.GetAsync(new OrderId(proposal.OrderId), ct);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        if (!order.AcceptsSubstitution)
        {
            return SubstitutionErrors.WrongOrderState;
        }

        var now = clock.GetUtcNow();
        var applied = proposal.MarkApplied(now);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        // Trava os estoques em ordem crescente de produto (a mesma de todo o sistema: sem deadlock).
        var original = new ProductId(proposal.OriginalProductId);
        var substitute = new ProductId(proposal.SubstituteProductId);
        foreach (var productId in new[] { original, substitute }.OrderBy(p => p.Value))
        {
            var stock = await inventory.GetForUpdateAsync(productId, ct);
            if (productId == substitute)
            {
                var sale = stock is null ? Result.Failure<StockChange>(OrderErrors.InsufficientStock(proposal.SubstituteName)) : stock.Sell(proposal.Quantity, now);
                if (sale.IsFailure)
                {
                    return OrderErrors.InsufficientStock(proposal.SubstituteName);
                }

                inventory.Add(StockMovement.Record(order.TenantId, substitute, proposal.SubstituteName, sale.Value,
                    $"Substituição no pedido {order.Id}", "substituicao", now, order.Id.Value, $"substitution:{proposal.Id}:out"));
            }
            else if (proposal.RestockOriginal && stock is not null)
            {
                var item = order.Items[proposal.ItemIndex];
                var change = stock.Receive(proposal.Quantity, null, item.UnitCostCents, "Retorno de substituição", "substituicao", now, StockMovementType.Devolucao);
                inventory.Add(StockMovement.Record(order.TenantId, original, proposal.OriginalName, change,
                    $"Substituição no pedido {order.Id} (retorno)", "substituicao", now, order.Id.Value, $"substitution:{proposal.Id}:in"));
            }
        }

        order.RecordSubstitution(new OrderSubstitution(proposal.ItemIndex, proposal.OriginalProductId.ToString(), proposal.OriginalName,
            proposal.SubstituteProductId.ToString(), proposal.SubstituteName, proposal.Quantity, proposal.Id, proposal.DecidedBy,
            proposal.RestockOriginal, now), now);
        return order;
    }
}

// ── Política ──

public sealed record GetSubstitutionPolicyQuery : IQuery<SubstitutionPolicyEnvelope>;

public sealed record PublishSubstitutionPolicyCommand(string? DefaultMode, int? PreapprovedCostTolerancePercent, int? ConsentWindowHours) : ICommand<SubstitutionPolicyEnvelope>;

internal sealed class PublishSubstitutionPolicyValidator : AbstractValidator<PublishSubstitutionPolicyCommand>
{
    public PublishSubstitutionPolicyValidator()
    {
        RuleFor(x => x.DefaultMode).Must(m => m is "exact" or "preapproved" or "ask_me").When(x => x.DefaultMode is not null).WithMessage("Modo inválido.");
        RuleFor(x => x.PreapprovedCostTolerancePercent).InclusiveBetween(0, 100).When(x => x.PreapprovedCostTolerancePercent is not null);
        RuleFor(x => x.ConsentWindowHours).InclusiveBetween(1, 168).When(x => x.ConsentWindowHours is not null);
    }
}

internal sealed class SubstitutionPolicyHandlers(ISubstitutionRepository repository, ITenantContext tenantContext, IUnitOfWork unitOfWork)
    : IQueryHandler<GetSubstitutionPolicyQuery, SubstitutionPolicyEnvelope>,
      ICommandHandler<PublishSubstitutionPolicyCommand, SubstitutionPolicyEnvelope>
{
    public async Task<Result<SubstitutionPolicyEnvelope>> Handle(GetSubstitutionPolicyQuery request, CancellationToken cancellationToken) =>
        await repository.GetPolicyAsync(cancellationToken) is { } policy
            ? new SubstitutionPolicyEnvelope(SubstitutionPolicyResponse.From(policy, persisted: true))
            : new SubstitutionPolicyEnvelope(SubstitutionPolicyResponse.From(SubstitutionPolicy.DefaultFor(tenantContext.RequiredTenantId), persisted: false));

    public async Task<Result<SubstitutionPolicyEnvelope>> Handle(PublishSubstitutionPolicyCommand request, CancellationToken cancellationToken)
    {
        var policy = await repository.GetPolicyAsync(cancellationToken);
        if (policy is null)
        {
            policy = SubstitutionPolicy.DefaultFor(tenantContext.RequiredTenantId);
            repository.Add(policy);
        }

        var published = policy.Publish(ParseMode(request.DefaultMode), request.PreapprovedCostTolerancePercent, request.ConsentWindowHours);
        if (published.IsFailure)
        {
            return published.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SubstitutionPolicyEnvelope(SubstitutionPolicyResponse.From(policy, persisted: true));
    }

    internal static SubstitutionMode? ParseMode(string? mode) => mode switch
    {
        "exact" => SubstitutionMode.Exact,
        "preapproved" => SubstitutionMode.Preapproved,
        "ask_me" => SubstitutionMode.AskMe,
        _ => null,
    };
}

// ── Propostas ──

/// <summary>
/// <c>POST /orders/{id}/resolutions/substitution</c> (rota do Nest). No Nest a troca era aplicada NA HORA, sem
/// consentimento. Aqui: pré-aprovado dentro da tolerância aplica na hora (mesmo contrato); o resto vira pergunta.
/// </summary>
public sealed record ProposeSubstitutionCommand(
    Guid OrderId, string? OriginalProductId, string? SubstituteProductId, int Quantity, string? Reason, bool RestockOriginal, string? IdempotencyKey, AuditActor Actor)
    : ICommand<SubstitutionOutcome>;

internal sealed class ProposeSubstitutionValidator : AbstractValidator<ProposeSubstitutionCommand>
{
    public ProposeSubstitutionValidator()
    {
        RuleFor(x => x.OriginalProductId).NotEmpty().Must(id => Guid.TryParse(id, out _)).WithMessage("Produto original inválido.");
        RuleFor(x => x.SubstituteProductId).NotEmpty().Must(id => Guid.TryParse(id, out _)).WithMessage("Produto substituto inválido.");
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().Length(3, 300);
        RuleFor(x => x.IdempotencyKey).MaximumLength(120);
    }
}

public sealed record CustomerConsentCommand(Guid ProposalId, string? Token, bool Approve) : ICommand<SubstitutionProposalEnvelope>;

internal sealed class CustomerConsentValidator : AbstractValidator<CustomerConsentCommand>
{
    public CustomerConsentValidator() => RuleFor(x => x.Token).NotEmpty().Length(16, 120).WithMessage("Link de resposta inválido.");
}

public sealed record OperatorDecisionCommand(Guid ProposalId, bool Approve, string? Reason, AuditActor Actor) : ICommand<SubstitutionProposalEnvelope>;

public sealed record ApplySubstitutionCommand(Guid ProposalId, AuditActor Actor) : ICommand<SubstitutionOutcome>;

public sealed record GetProposalByTokenQuery(Guid ProposalId, string? Token) : IQuery<SubstitutionProposalEnvelope>;

public sealed record ListOrderSubstitutionsQuery(Guid OrderId) : IQuery<SubstitutionProposalList>;

public sealed record ExpireSubstitutionCommand(Guid ProposalId) : ICommand<bool>;

internal sealed partial class SubstitutionHandlers(
    ISubstitutionRepository substitutions,
    IOrderRepository orders,
    ICatalogRepository catalog,
    SubstitutionApplier applier,
    ITenantContext tenantContext,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<SubstitutionHandlers> logger)
    : ICommandHandler<ProposeSubstitutionCommand, SubstitutionOutcome>,
      ICommandHandler<CustomerConsentCommand, SubstitutionProposalEnvelope>,
      ICommandHandler<OperatorDecisionCommand, SubstitutionProposalEnvelope>,
      ICommandHandler<ApplySubstitutionCommand, SubstitutionOutcome>,
      IQueryHandler<GetProposalByTokenQuery, SubstitutionProposalEnvelope>,
      IQueryHandler<ListOrderSubstitutionsQuery, SubstitutionProposalList>,
      ICommandHandler<ExpireSubstitutionCommand, bool>
{
    public async Task<Result<SubstitutionOutcome>> Handle(ProposeSubstitutionCommand request, CancellationToken cancellationToken)
    {
        var key = $"sub:{request.OrderId}:{request.IdempotencyKey ?? Guid.NewGuid().ToString()}";
        if (await substitutions.FindByKeyAsync(key, cancellationToken) is { } replay)
        {
            return await OutcomeAsync(replay.Id, null, cancellationToken); // mesmo clique de novo: mesma proposta, sem novo token
        }

        var order = await orders.GetAsync(new OrderId(request.OrderId), cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        if (!order.AcceptsSubstitution)
        {
            return SubstitutionErrors.WrongOrderState;
        }

        var itemIndex = order.Items.Select((item, index) => (item, index))
            .FirstOrDefault(x => x.item.ProductId.ToString() == request.OriginalProductId && x.item.Quantity >= request.Quantity, (null!, -1)).index;
        if (itemIndex < 0)
        {
            return SubstitutionErrors.ItemNotInOrder;
        }

        if (await catalog.GetProductAsync(new ProductId(Guid.Parse(request.SubstituteProductId!)), cancellationToken) is not { IsActive: true } substitute)
        {
            return OrderErrors.ProductUnavailable(request.SubstituteProductId!);
        }

        var item = order.Items[itemIndex];
        var policy = await substitutions.GetPolicyAsync(cancellationToken) ?? SubstitutionPolicy.DefaultFor(tenantContext.RequiredTenantId);
        var now = clock.GetUtcNow();
        var proposed = SubstitutionProposal.Propose(new ProposalInput(order.TenantId, order.Id, itemIndex, item.ProductId, item.Name, item.UnitPriceCents,
            substitute.Id, substitute.Name, substitute.EffectivePrice.Cents, request.Quantity, order.SubstitutionMode, request.Reason!, key, request.RestockOriginal), policy, now);
        if (proposed.IsFailure)
        {
            return proposed.Error;
        }

        var (proposal, token) = proposed.Value;
        try
        {
            var written = await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                substitutions.Add(proposal);
                audit.Add(AuditEntry.Record(order.TenantId, "order.substitution_proposed", request.Actor, "order", order.Id.ToString(),
                    $"{item.Name} → {substitute.Name} ({request.Quantity}×, {proposal.ValueDeltaPercent}%): {proposal.State.ToString().ToLowerInvariant()}", now));
                await unitOfWork.SaveChangesAsync(ct);

                if (proposal.State == ProposalState.Approved)
                {
                    // Pré-aprovado dentro da tolerância: aplica na MESMA transação (como a rota do Nest fazia).
                    var applied = await applier.ApplyAsync(proposal.Id, ct);
                    if (applied.IsFailure)
                    {
                        return Result.Failure<bool>(applied.Error);
                    }

                    await unitOfWork.SaveChangesAsync(ct);
                }

                return Result.Success(true);
            }, cancellationToken);
            if (written.IsFailure)
            {
                return written.Error;
            }
        }
        catch (UniqueConstraintViolationException)
        {
            return await substitutions.FindByKeyAsync(key, cancellationToken) is { } raced
                ? await OutcomeAsync(raced.Id, null, cancellationToken)
                : SubstitutionErrors.NotFound;
        }

        return await OutcomeAsync(proposal.Id, token, cancellationToken);
    }

    public async Task<Result<SubstitutionProposalEnvelope>> Handle(CustomerConsentCommand request, CancellationToken cancellationToken)
    {
        Error? afterCommit = null;
        var decided = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var proposal = await substitutions.GetForUpdateAsync(request.ProposalId, ct);
            if (proposal is null)
            {
                return Result.Failure<SubstitutionProposal>(SubstitutionErrors.NotFound);
            }

            var result = proposal.DecideByCustomer(request.Token!, request.Approve, clock.GetUtcNow());
            if (result.IsFailure && result.Error != SubstitutionErrors.Expired)
            {
                return Result.Failure<SubstitutionProposal>(result.Error);
            }

            afterCommit = result.IsFailure ? result.Error : null; // vencida: grava a expiração e devolve o erro depois do commit
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(proposal);
        }, cancellationToken);
        if (afterCommit is not null)
        {
            return afterCommit;
        }

        if (decided.IsFailure)
        {
            return decided.Error;
        }

        if (decided.Value.State == ProposalState.Approved)
        {
            await TryApplyAsync(decided.Value.Id, cancellationToken); // a loja já queria a troca: aplica, se o estoque deixar
        }

        return new SubstitutionProposalEnvelope(SubstitutionProposalResponse.From((await substitutions.GetAsync(request.ProposalId, cancellationToken))!));
    }

    public async Task<Result<SubstitutionProposalEnvelope>> Handle(OperatorDecisionCommand request, CancellationToken cancellationToken)
    {
        var decided = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var proposal = await substitutions.GetForUpdateAsync(request.ProposalId, ct);
            if (proposal is null)
            {
                return Result.Failure<SubstitutionProposal>(SubstitutionErrors.NotFound);
            }

            var now = clock.GetUtcNow();
            var result = proposal.DecideByOperator(request.Approve, request.Actor.Role, request.Reason, now);
            if (result.IsFailure)
            {
                return Result.Failure<SubstitutionProposal>(result.Error);
            }

            audit.Add(AuditEntry.Record(proposal.TenantId, "order.substitution_decided", request.Actor, "order", proposal.OrderId.ToString(),
                $"Troca {proposal.OriginalName} → {proposal.SubstituteName}: {(request.Approve ? "aprovada" : "recusada")} pela loja", now));
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(proposal);
        }, cancellationToken);
        return decided.IsFailure ? decided.Error : new SubstitutionProposalEnvelope(SubstitutionProposalResponse.From(decided.Value));
    }

    public async Task<Result<SubstitutionOutcome>> Handle(ApplySubstitutionCommand request, CancellationToken cancellationToken)
    {
        var applied = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var result = await applier.ApplyAsync(request.ProposalId, ct);
            if (result.IsFailure)
            {
                return Result.Failure<bool>(result.Error);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(true);
        }, cancellationToken);
        return applied.IsFailure ? applied.Error : await OutcomeAsync(request.ProposalId, null, cancellationToken);
    }

    public async Task<Result<SubstitutionProposalEnvelope>> Handle(GetProposalByTokenQuery request, CancellationToken cancellationToken)
    {
        var proposal = await substitutions.GetAsync(request.ProposalId, cancellationToken);
        return proposal is not null && proposal.CanBeReadWith(request.Token)
            ? new SubstitutionProposalEnvelope(SubstitutionProposalResponse.From(proposal))
            : SubstitutionErrors.NotFound; // "não existe" e "token errado" respondem igual
    }

    public async Task<Result<SubstitutionProposalList>> Handle(ListOrderSubstitutionsQuery request, CancellationToken cancellationToken) =>
        new SubstitutionProposalList([.. (await substitutions.ByOrderAsync(request.OrderId, cancellationToken)).Select(SubstitutionProposalResponse.From)]);

    public async Task<Result<bool>> Handle(ExpireSubstitutionCommand request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var proposal = await substitutions.GetForUpdateAsync(request.ProposalId, ct);
            var expired = proposal?.ExpireIfDue(clock.GetUtcNow()) ?? false;
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(expired);
        }, cancellationToken);

    private async Task TryApplyAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        var applied = await Handle(new ApplySubstitutionCommand(proposalId, new AuditActor(null, "customer", null, string.Empty)), cancellationToken);
        if (applied.IsFailure)
        {
            LogApplyLater(logger, proposalId, applied.Error.Message); // a aprovação fica; a loja aplica quando puder
        }
    }

    private async Task<Result<SubstitutionOutcome>> OutcomeAsync(Guid proposalId, string? token, CancellationToken cancellationToken)
    {
        var proposal = await substitutions.GetAsync(proposalId, cancellationToken);
        var order = proposal is null ? null : await orders.GetAsync(new OrderId(proposal.OrderId), cancellationToken);
        return proposal is null || order is null
            ? SubstitutionErrors.NotFound
            : new SubstitutionOutcome(OrderResponse.From(order, internalView: true), SubstitutionProposalResponse.From(proposal), token);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Troca {ProposalId} aprovada pelo cliente, mas não aplicada agora: {Reason}. Fica para a loja aplicar.")]
    private static partial void LogApplyLater(ILogger logger, Guid proposalId, string reason);
}
