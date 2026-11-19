using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.CustomOrders;
using Floruna.Domain.Ordering;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.CustomOrders;

// ── Contrato (Nest) ──

public sealed record CustomOrderRequestResponse(
    string Id, string CustomerName, string CustomerPhone, string CustomerEmail, string? Occasion, string ContainerType, string Size,
    IReadOnlyList<string> ColorPalette, IReadOnlyList<string> FlowerPreferences, string Ribbon, string CardMessage, string? BudgetRange,
    string? DesiredDate, string Notes, string Status, long? QuotedPrice, string AdminNotes, string CreatedAt, string UpdatedAt)
{
    public static CustomOrderRequestResponse From(CustomOrderRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Id.ToString(), r.CustomerName, r.CustomerPhone, r.CustomerEmail, r.Occasion, r.ContainerType, r.Size, r.ColorPalette,
            r.FlowerPreferences, r.Ribbon, r.CardMessage, r.BudgetRange, r.DesiredDate is { } d ? CatalogMapping.Iso(d) : null, r.Notes, r.Status,
            r.QuotedPrice, r.AdminNotes, CatalogMapping.Iso(r.CreatedAt), CatalogMapping.Iso(r.UpdatedAt));
    }
}

public sealed record CustomOrderEnvelope(CustomOrderRequestResponse Request);

public sealed record CustomOrderPage(IReadOnlyList<CustomOrderRequestResponse> Requests, int Total, int Page, int Limit, int Pages);

public sealed record QuoteLineDto(string Description, int Quantity, long UnitPrice);

/// <remarks>O hash do token NUNCA sai da API.</remarks>
public sealed record CustomQuoteResponse(
    string Id, string RequestId, int Version, IReadOnlyList<QuoteLineDto> Lines, long Total, string Notes, string State, string? ValidUntil,
    string? PublishedAt, string? DecidedAt, string? ConvertedOrderId, string DecisionReason, string CreatedAt, string UpdatedAt)
{
    public static CustomQuoteResponse From(CustomQuote q)
    {
        ArgumentNullException.ThrowIfNull(q);
        return new(q.Id.ToString(), q.RequestId.ToString(), q.Version, [.. q.Lines.Select(l => new QuoteLineDto(l.Description, l.Quantity, l.UnitPrice))],
            q.Total, q.Notes, q.State.ToString().ToLowerInvariant(), Iso(q.ValidUntil), Iso(q.PublishedAt), Iso(q.DecidedAt),
            q.ConvertedOrderId?.ToString(), q.DecisionReason, CatalogMapping.Iso(q.CreatedAt), CatalogMapping.Iso(q.UpdatedAt));
    }

    private static string? Iso(DateTimeOffset? value) => value is { } v ? CatalogMapping.Iso(v) : null;
}

public sealed record CustomQuoteEnvelope(CustomQuoteResponse CustomQuote);

public sealed record CustomQuoteList(IReadOnlyList<CustomQuoteResponse> CustomQuotes);

/// <summary>O token do link aparece só nesta resposta.</summary>
public sealed record PublishedQuoteEnvelope(CustomQuoteResponse CustomQuote, string ResponseToken);

public interface ICustomOrderQueries
{
    Task<(IReadOnlyList<CustomOrderRequestResponse> Items, int Total)> ListAsync(int page, int limit, string? status, CancellationToken cancellationToken);

    /// <summary>Orçamentos publicados e vencidos, de TODAS as lojas (job): (loja, orçamento).</summary>
    Task<IReadOnlyList<(Guid TenantId, Guid QuoteId)>> DueQuotesAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}

// ── Solicitação ──

public sealed record SubmitCustomOrderCommand(
    string? CustomerName, string? CustomerPhone, string? CustomerEmail, string? Occasion, string? ContainerType, string? Size,
    IReadOnlyList<string>? ColorPalette, IReadOnlyList<string>? FlowerPreferences, string? Ribbon, string? CardMessage, string? BudgetRange,
    DateTimeOffset? DesiredDate, string? Notes) : ICommand<CustomOrderEnvelope>;

internal sealed class SubmitCustomOrderValidator : AbstractValidator<SubmitCustomOrderCommand>
{
    public SubmitCustomOrderValidator()
    {
        RuleFor(x => x.CustomerName).NotEmpty().Length(2, 80);
        RuleFor(x => x.CustomerPhone).NotEmpty().Length(8, 20);
        RuleFor(x => x.CustomerEmail).EmailAddress().When(x => !string.IsNullOrEmpty(x.CustomerEmail)).WithMessage("E-mail inválido.");
        RuleFor(x => x.Occasion).Must(CustomOrderOptions.Occasions.Contains!).When(x => x.Occasion is not null).WithMessage("Ocasião inválida.");
        RuleFor(x => x.ContainerType).Must(v => v is not null && CustomOrderOptions.Containers.Contains(v)).WithMessage("Recipiente inválido.");
        RuleFor(x => x.Size).Must(v => v is not null && CustomOrderOptions.Sizes.Contains(v)).WithMessage("Tamanho inválido.");
        RuleForEach(x => x.ColorPalette).Must(CustomOrderOptions.ColorPalettes.Contains).WithMessage("Paleta inválida.");
        RuleForEach(x => x.FlowerPreferences).Must(CustomOrderOptions.FlowerPreferences.Contains).WithMessage("Preferência de flor inválida.");
        RuleFor(x => x.BudgetRange).Must(CustomOrderOptions.BudgetRanges.Contains!).When(x => x.BudgetRange is not null).WithMessage("Faixa de orçamento inválida.");
        RuleFor(x => x.Ribbon).MaximumLength(80);
        RuleFor(x => x.CardMessage).MaximumLength(300);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public sealed record ListCustomOrdersQuery(int Page, int Limit, string? Status) : IQuery<CustomOrderPage>;

public sealed record GetCustomOrderQuery(Guid Id) : IQuery<CustomOrderEnvelope>;

public sealed record AnnotateCustomOrderCommand(Guid Id, string? Status, long? QuotedPrice, string? AdminNotes) : ICommand<CustomOrderEnvelope>;

internal sealed class AnnotateCustomOrderValidator : AbstractValidator<AnnotateCustomOrderCommand>
{
    public AnnotateCustomOrderValidator()
    {
        RuleFor(x => x.Status).Must(CustomOrderOptions.Statuses.Contains!).When(x => x.Status is not null).WithMessage("Status inválido.");
        RuleFor(x => x.QuotedPrice).GreaterThanOrEqualTo(0).When(x => x.QuotedPrice is not null);
        RuleFor(x => x.AdminNotes).MaximumLength(1000);
    }
}

internal sealed class CustomOrderRequestHandlers(ICustomOrderRepository repository, ICustomOrderQueries queries, ITenantContext tenantContext, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SubmitCustomOrderCommand, CustomOrderEnvelope>,
      IQueryHandler<ListCustomOrdersQuery, CustomOrderPage>,
      IQueryHandler<GetCustomOrderQuery, CustomOrderEnvelope>,
      ICommandHandler<AnnotateCustomOrderCommand, CustomOrderEnvelope>
{
    public async Task<Result<CustomOrderEnvelope>> Handle(SubmitCustomOrderCommand r, CancellationToken cancellationToken)
    {
        var request = CustomOrderRequest.Submit(tenantContext.RequiredTenantId, new CustomOrderSpec(r.CustomerName!, r.CustomerPhone!, r.CustomerEmail,
            r.Occasion, r.ContainerType!, r.Size!, r.ColorPalette ?? [], r.FlowerPreferences ?? [], r.Ribbon, r.CardMessage, r.BudgetRange, r.DesiredDate, r.Notes),
            clock.GetUtcNow());
        repository.Add(request);
        await unitOfWork.SaveChangesAsync(cancellationToken); // o aviso à loja sai pela outbox (CustomOrderRequested)
        return new CustomOrderEnvelope(CustomOrderRequestResponse.From(request));
    }

    public async Task<Result<CustomOrderPage>> Handle(ListCustomOrdersQuery request, CancellationToken cancellationToken)
    {
        var (items, total) = await queries.ListAsync(request.Page, request.Limit, request.Status, cancellationToken);
        return new CustomOrderPage(items, total, request.Page, request.Limit, (int)Math.Ceiling(total / (double)request.Limit));
    }

    public async Task<Result<CustomOrderEnvelope>> Handle(GetCustomOrderQuery request, CancellationToken cancellationToken) =>
        await repository.GetAsync(request.Id, cancellationToken) is { } found
            ? new CustomOrderEnvelope(CustomOrderRequestResponse.From(found))
            : QuoteErrors.RequestNotFound;

    public async Task<Result<CustomOrderEnvelope>> Handle(AnnotateCustomOrderCommand request, CancellationToken cancellationToken)
    {
        var found = await repository.GetAsync(request.Id, cancellationToken);
        if (found is null)
        {
            return QuoteErrors.RequestNotFound;
        }

        found.Annotate(request.Status, request.QuotedPrice, request.AdminNotes, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CustomOrderEnvelope(CustomOrderRequestResponse.From(found));
    }
}

// ── Orçamentos ──

public sealed record QuoteContent(IReadOnlyList<QuoteLineDto>? Lines, string? Notes);

internal sealed class QuoteContentValidator : AbstractValidator<QuoteContent>
{
    public QuoteContentValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Informe ao menos um item do orçamento.").Must(l => l is null || l.Count <= 30).WithMessage("No máximo 30 itens.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.Description).NotEmpty().Length(3, 200);
            l.RuleFor(x => x.Quantity).GreaterThanOrEqualTo(1);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        });
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public sealed record ListQuotesQuery(Guid RequestId) : IQuery<CustomQuoteList>;

public sealed record DraftQuoteCommand(Guid RequestId, QuoteContent Content) : ICommand<CustomQuoteEnvelope>;

public sealed record UpdateQuoteDraftCommand(Guid QuoteId, QuoteContent Content) : ICommand<CustomQuoteEnvelope>;

public sealed record PublishQuoteCommand(Guid QuoteId, int? ValidityDays) : ICommand<PublishedQuoteEnvelope>;

public sealed record ConvertQuoteCommand(Guid QuoteId, Guid OrderId) : ICommand<CustomQuoteEnvelope>;

/// <summary>Resposta do CLIENTE pelo link (público, autorizado pelo token).</summary>
public sealed record DecideQuoteCommand(Guid QuoteId, string? Token, bool Accept, string? Reason) : ICommand<CustomQuoteEnvelope>;

/// <summary>Leitura pública do orçamento pelo link (no Nest o cliente não tinha como VER a proposta pela API).</summary>
public sealed record GetQuoteByTokenQuery(Guid QuoteId, string? Token) : IQuery<CustomQuoteEnvelope>;

public sealed record ExpireQuoteCommand(Guid QuoteId) : ICommand<bool>;

internal sealed class DraftQuoteValidator : AbstractValidator<DraftQuoteCommand>
{
    public DraftQuoteValidator() => RuleFor(x => x.Content).SetValidator(new QuoteContentValidator());
}

internal sealed class UpdateQuoteDraftValidator : AbstractValidator<UpdateQuoteDraftCommand>
{
    public UpdateQuoteDraftValidator() => RuleFor(x => x.Content).SetValidator(new QuoteContentValidator());
}

internal sealed class PublishQuoteValidator : AbstractValidator<PublishQuoteCommand>
{
    public PublishQuoteValidator() => RuleFor(x => x.ValidityDays).InclusiveBetween(1, 90).When(x => x.ValidityDays is not null);
}

internal sealed class DecideQuoteValidator : AbstractValidator<DecideQuoteCommand>
{
    public DecideQuoteValidator()
    {
        RuleFor(x => x.Token).NotEmpty().Length(16, 120).WithMessage("Link de resposta inválido.");
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}

/// <summary>
/// Toda operação TRAVA a solicitação (<see cref="ICustomOrderRepository.LockQuoteAsync"/>): numeração de
/// versão, "publicar substitui a anterior" e "aceitar só uma" são decididos em fila, por solicitação.
/// </summary>
internal sealed class CustomQuoteHandlers(ICustomOrderRepository repository, IOrderRepository orders, IUnitOfWork unitOfWork, TimeProvider clock)
    : IQueryHandler<ListQuotesQuery, CustomQuoteList>,
      ICommandHandler<DraftQuoteCommand, CustomQuoteEnvelope>,
      ICommandHandler<UpdateQuoteDraftCommand, CustomQuoteEnvelope>,
      ICommandHandler<PublishQuoteCommand, PublishedQuoteEnvelope>,
      ICommandHandler<ConvertQuoteCommand, CustomQuoteEnvelope>,
      ICommandHandler<DecideQuoteCommand, CustomQuoteEnvelope>,
      IQueryHandler<GetQuoteByTokenQuery, CustomQuoteEnvelope>,
      ICommandHandler<ExpireQuoteCommand, bool>
{
    public async Task<Result<CustomQuoteList>> Handle(ListQuotesQuery request, CancellationToken cancellationToken) =>
        new CustomQuoteList([.. (await repository.QuotesOfAsync(request.RequestId, cancellationToken)).Select(CustomQuoteResponse.From)]);

    public async Task<Result<CustomQuoteEnvelope>> Handle(DraftQuoteCommand request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var owner = await repository.GetForUpdateAsync(request.RequestId, ct);
            if (owner is null)
            {
                return Result.Failure<CustomQuoteEnvelope>(QuoteErrors.RequestNotFound);
            }

            var existing = await repository.QuotesOfAsync(owner.Id, ct);
            var drafted = CustomQuote.Draft(owner.TenantId, owner.Id, existing.Count == 0 ? 1 : existing.Max(q => q.Version) + 1,
                Lines(request.Content), request.Content.Notes, clock.GetUtcNow());
            if (drafted.IsFailure)
            {
                return Result.Failure<CustomQuoteEnvelope>(drafted.Error);
            }

            repository.Add(drafted.Value);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(new CustomQuoteEnvelope(CustomQuoteResponse.From(drafted.Value)));
        }, cancellationToken);

    public async Task<Result<CustomQuoteEnvelope>> Handle(UpdateQuoteDraftCommand request, CancellationToken cancellationToken) =>
        await InLockAsync(request.QuoteId, (_, quote, now) => quote.UpdateDraft(Lines(request.Content), request.Content.Notes, now), cancellationToken);

    public async Task<Result<PublishedQuoteEnvelope>> Handle(PublishQuoteCommand request, CancellationToken cancellationToken)
    {
        string? token = null;
        var result = await InLockAsync(request.QuoteId, async (owner, quote, now) =>
        {
            var siblings = (await repository.QuotesOfAsync(owner.Id, CancellationToken.None)).Where(q => q.Id != quote.Id).ToList();
            if (siblings.Any(q => q.State is QuoteState.Accepted or QuoteState.Converted))
            {
                return QuoteErrors.AlreadyAcceptedVersion; // o cliente já fechou negócio: outra proposta não faz sentido
            }

            var published = quote.Publish(request.ValidityDays ?? CustomQuote.DefaultValidityDays, now);
            if (published.IsFailure)
            {
                return published.Error;
            }

            token = published.Value;
            foreach (var older in siblings)
            {
                older.Supersede(quote.Version, now); // o preço antigo deixa de valer
            }

            owner.QuoteChanged(QuoteState.Published, quote.Total, now);
            return Result.Success();
        }, cancellationToken);
        return result.IsFailure ? result.Error : new PublishedQuoteEnvelope(result.Value.CustomQuote, token!);
    }

    public async Task<Result<CustomQuoteEnvelope>> Handle(ConvertQuoteCommand request, CancellationToken cancellationToken)
    {
        if (await orders.GetAsync(new OrderId(request.OrderId), cancellationToken) is null)
        {
            return OrderErrors.NotFound;
        }

        try
        {
            return await InLockAsync(request.QuoteId, (owner, quote, now) =>
            {
                var converted = quote.Convert(new OrderId(request.OrderId), now);
                if (converted.IsSuccess)
                {
                    owner.QuoteChanged(QuoteState.Converted, quote.Total, now);
                }

                return converted;
            }, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return QuoteErrors.OrderAlreadyLinked; // o mesmo pedido já está ligado a outro orçamento
        }
    }

    public async Task<Result<CustomQuoteEnvelope>> Handle(DecideQuoteCommand request, CancellationToken cancellationToken)
    {
        // Resposta fora do prazo precisa GRAVAR a expiração e ainda assim devolver erro: um Result de falha
        // desfaria a transação. Então a expiração é confirmada e o erro sai depois do commit.
        Error? afterCommit = null;
        var result = await InLockAsync(request.QuoteId, (owner, quote, now) =>
        {
            var decided = quote.Decide(request.Token!, request.Accept, request.Reason, now);
            if (decided.IsFailure && decided.Error == QuoteErrors.Expired)
            {
                afterCommit = decided.Error;
                return Result.Success();
            }

            if (decided.IsSuccess)
            {
                owner.QuoteChanged(quote.State, quote.Total, now);
            }

            return decided;
        }, cancellationToken);
        if (afterCommit is not null)
        {
            return afterCommit; // a expiração ficou gravada; o cliente recebe o erro
        }

        return result;
    }

    public async Task<Result<CustomQuoteEnvelope>> Handle(GetQuoteByTokenQuery request, CancellationToken cancellationToken)
    {
        var quote = await repository.GetQuoteAsync(request.QuoteId, cancellationToken);
        // Mesmo erro para "não existe" e "token errado": não revela quais IDs existem.
        return quote is not null && quote.State != QuoteState.Draft && quote.CanBeReadWith(request.Token)
            ? new CustomQuoteEnvelope(CustomQuoteResponse.From(quote))
            : QuoteErrors.NotFound;
    }

    public async Task<Result<bool>> Handle(ExpireQuoteCommand request, CancellationToken cancellationToken)
    {
        var expired = false;
        var result = await InLockAsync(request.QuoteId, (_, quote, now) =>
        {
            expired = quote.ExpireIfDue(now);
            return Result.Success();
        }, cancellationToken);
        return result.IsFailure ? result.Error : expired;
    }

    private Task<Result<CustomQuoteEnvelope>> InLockAsync(Guid quoteId, Func<CustomOrderRequest, CustomQuote, DateTimeOffset, Result> change, CancellationToken cancellationToken) =>
        InLockAsync(quoteId, (owner, quote, now) => Task.FromResult(change(owner, quote, now)), cancellationToken);

    private async Task<Result<CustomQuoteEnvelope>> InLockAsync(
        Guid quoteId, Func<CustomOrderRequest, CustomQuote, DateTimeOffset, Task<Result>> change, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (await repository.LockQuoteAsync(quoteId, ct) is not var (owner, quote))
            {
                return Result.Failure<CustomQuoteEnvelope>(QuoteErrors.NotFound);
            }

            var changed = await change(owner, quote, clock.GetUtcNow());
            if (changed.IsFailure)
            {
                return Result.Failure<CustomQuoteEnvelope>(changed.Error);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(new CustomQuoteEnvelope(CustomQuoteResponse.From(quote)));
        }, cancellationToken);

    private static List<QuoteLine> Lines(QuoteContent content) =>
        [.. (content.Lines ?? []).Select(l => new QuoteLine(l.Description, l.Quantity, l.UnitPrice))];
}
