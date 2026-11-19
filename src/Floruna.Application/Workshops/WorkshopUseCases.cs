using System.Text.RegularExpressions;
using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.Workshops;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Workshops;

// Contrato do Nest: o recurso se chama "event".

public sealed record EventResponse(
    string Id, string Slug, string Title, string ShortDescription, string Description, string Date, string Location, string Image,
    long Price, int Capacity, int Registered, bool IsActive, string CreatedAt, string UpdatedAt)
{
    public static EventResponse From(Workshop w)
    {
        ArgumentNullException.ThrowIfNull(w);
        return new(w.Id.ToString(), w.Slug, w.Title, w.ShortDescription, w.Description, w.Date, w.Location, w.Image, w.PriceCents,
            w.Capacity, w.Registered, w.IsActive, CatalogMapping.Iso(w.CreatedAt), CatalogMapping.Iso(w.UpdatedAt));
    }
}

public sealed record EventEnvelope(EventResponse Event);

public sealed record EventPage(IReadOnlyList<EventResponse> Events, int Total, int Page, int Limit, int Pages);

public sealed record RegistrationResponse(string Id, string EventId, string Name, string Phone, string? Email, string CreatedAt);

public sealed record RegistrationPage(IReadOnlyList<RegistrationResponse> Registrations, int Total, int Page, int Limit, int Pages);

public sealed record EventInput(
    string? Title, string? Slug, string? ShortDescription, string? Description, string? Date, string? Location, string? Image,
    long? Price, int? Capacity, bool? IsActive);

/// <summary>Leituras (lado Q do CQRS).</summary>
public interface IWorkshopQueries
{
    Task<(IReadOnlyList<EventResponse> Items, int Total)> ListAsync(int page, int limit, bool onlyActive, CancellationToken cancellationToken);

    Task<EventResponse?> BySlugAsync(string slug, CancellationToken cancellationToken);

    Task<(IReadOnlyList<RegistrationResponse> Items, int Total)> RegistrationsAsync(Guid workshopId, int page, int limit, CancellationToken cancellationToken);
}

/// <summary>Regras do evento. Abstrata: o registro automático de validadores só instancia as concretas.</summary>
internal abstract partial class EventInputValidatorBase : AbstractValidator<EventInput>
{
    /// <param name="partial">PUT parcial: só valida o que veio.</param>
    protected EventInputValidatorBase(bool partial)
    {
        RuleFor(x => x.Title).NotEmpty().Length(2, 120).When(x => !partial || x.Title is not null);
        RuleFor(x => x.Slug).Matches(SlugPattern()).When(x => !string.IsNullOrEmpty(x.Slug)).WithMessage("Slug inválido.");
        RuleFor(x => x.ShortDescription).NotEmpty().Length(10, 200).When(x => !partial || x.ShortDescription is not null);
        RuleFor(x => x.Description).NotEmpty().MinimumLength(10).MaximumLength(5_000).When(x => !partial || x.Description is not null);
        RuleFor(x => x.Date).NotEmpty().MaximumLength(60).When(x => !partial || x.Date is not null);
        RuleFor(x => x.Location).NotEmpty().Length(2, 200).When(x => !partial || x.Location is not null);
        RuleFor(x => x.Image).NotEmpty().MaximumLength(500).When(x => !partial || x.Image is not null);
        RuleFor(x => x.Price).NotNull().GreaterThanOrEqualTo(0).When(x => !partial || x.Price is not null);
        RuleFor(x => x.Capacity).NotNull().GreaterThanOrEqualTo(1).When(x => !partial || x.Capacity is not null);
        RuleFor(x => x.IsActive).NotNull().When(x => !partial);
    }

    [GeneratedRegex("^[a-z0-9-]*$")]
    private static partial Regex SlugPattern();
}

internal sealed class FullEventInputValidator() : EventInputValidatorBase(partial: false);

internal sealed class PartialEventInputValidator() : EventInputValidatorBase(partial: true);

public sealed record CreateEventCommand(EventInput Input) : ICommand<EventEnvelope>;

public sealed record UpdateEventCommand(Guid Id, EventInput Input) : ICommand<EventEnvelope>;

public sealed record DeleteEventCommand(Guid Id) : ICommand<bool>;

internal sealed class CreateEventValidator : AbstractValidator<CreateEventCommand>
{
    public CreateEventValidator() => RuleFor(x => x.Input).SetValidator(new FullEventInputValidator());
}

internal sealed class UpdateEventValidator : AbstractValidator<UpdateEventCommand>
{
    public UpdateEventValidator() => RuleFor(x => x.Input).SetValidator(new PartialEventInputValidator());
}

internal sealed class WorkshopCommandHandlers(IWorkshopRepository workshops, ITenantContext tenantContext, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateEventCommand, EventEnvelope>,
      ICommandHandler<UpdateEventCommand, EventEnvelope>,
      ICommandHandler<DeleteEventCommand, bool>
{
    public async Task<Result<EventEnvelope>> Handle(CreateEventCommand request, CancellationToken cancellationToken)
    {
        var i = request.Input;
        var created = Workshop.Create(tenantContext.RequiredTenantId,
            new WorkshopSpec(i.Title!, i.Slug, i.ShortDescription!, i.Description!, i.Date!, i.Location!, i.Image!, i.Price!.Value, i.Capacity!.Value, i.IsActive!.Value),
            clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await workshops.SlugExistsAsync(created.Value.Slug, null, cancellationToken))
        {
            return WorkshopErrors.SlugTaken(created.Value.Slug);
        }

        workshops.Add(created.Value);
        return await SaveAsync(created.Value, cancellationToken);
    }

    public async Task<Result<EventEnvelope>> Handle(UpdateEventCommand request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Travada: uma inscrição concorrente não passa da capacidade nova entre a checagem e o commit.
            var workshop = await workshops.GetForUpdateAsync(request.Id, ct);
            if (workshop is null)
            {
                return Result.Failure<EventEnvelope>(WorkshopErrors.NotFound);
            }

            var i = request.Input;
            var updated = workshop.Update(new WorkshopSpec(i.Title ?? workshop.Title, i.Slug, i.ShortDescription ?? workshop.ShortDescription,
                i.Description ?? workshop.Description, i.Date ?? workshop.Date, i.Location ?? workshop.Location, i.Image ?? workshop.Image,
                i.Price ?? workshop.PriceCents, i.Capacity ?? workshop.Capacity, i.IsActive ?? workshop.IsActive), clock.GetUtcNow());
            if (updated.IsFailure)
            {
                return Result.Failure<EventEnvelope>(updated.Error);
            }

            if (await workshops.SlugExistsAsync(workshop.Slug, workshop.Id, ct))
            {
                return Result.Failure<EventEnvelope>(WorkshopErrors.SlugTaken(workshop.Slug));
            }

            return await SaveAsync(workshop, ct);
        }, cancellationToken);

    public async Task<Result<bool>> Handle(DeleteEventCommand request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var workshop = await workshops.GetForUpdateAsync(request.Id, ct);
            if (workshop is null)
            {
                return Result.Failure<bool>(WorkshopErrors.NotFound);
            }

            var removable = workshop.EnsureCanBeRemoved();
            if (removable.IsFailure)
            {
                return Result.Failure<bool>(removable.Error);
            }

            workshops.Remove(workshop);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(true);
        }, cancellationToken);

    private async Task<Result<EventEnvelope>> SaveAsync(Workshop workshop, CancellationToken ct)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
            return new EventEnvelope(EventResponse.From(workshop));
        }
        catch (UniqueConstraintViolationException)
        {
            return WorkshopErrors.SlugTaken(workshop.Slug); // corrida: o índice único decidiu
        }
    }
}

/// <summary>Inscrição pública (<c>POST /events/:id/registrations</c>).</summary>
public sealed record RegisterForEventCommand(Guid EventId, string? Name, string? Phone, string? Email) : ICommand<EventEnvelope>;

internal sealed partial class RegisterForEventValidator : AbstractValidator<RegisterForEventCommand>
{
    public RegisterForEventValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 120);
        RuleFor(x => x.Phone).NotEmpty().Matches(Phone()).WithMessage("Telefone inválido. Use (11) 99999-9999.");
        RuleFor(x => x.Email).EmailAddress().MaximumLength(254).When(x => !string.IsNullOrEmpty(x.Email)).WithMessage("E-mail inválido.");
    }

    [GeneratedRegex(@"^\(?\d{2}\)?\s?\d{4,5}-?\d{4}$")]
    private static partial Regex Phone();
}

/// <summary>
/// No Nest: reserva a vaga ($inc condicional), grava a inscrição e, se a gravação falhar,
/// devolve a vaga (compensação). Aqui as duas coisas estão na MESMA transação: não existe
/// janela em que a vaga foi ocupada sem inscrição, nem compensação que possa falhar.
/// </summary>
internal sealed class RegisterForEventHandler(IWorkshopRepository workshops, ITenantContext tenantContext, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RegisterForEventCommand, EventEnvelope>
{
    public async Task<Result<EventEnvelope>> Handle(RegisterForEventCommand request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (!await workshops.TryTakeSeatAsync(request.EventId, ct))
            {
                var existing = await workshops.GetAsync(request.EventId, ct);
                return Result.Failure<EventEnvelope>(existing is { IsActive: true } ? WorkshopErrors.SoldOut : WorkshopErrors.NotFound);
            }

            workshops.Add(new WorkshopRegistration(tenantContext.RequiredTenantId, request.EventId, request.Name!, request.Phone!, request.Email, clock.GetUtcNow()));
            await unitOfWork.SaveChangesAsync(ct);
            var workshop = await workshops.GetAsync(request.EventId, ct);
            return Result.Success(new EventEnvelope(EventResponse.From(workshop!)));
        }, cancellationToken);
}

/// <param name="IncludeInactive">Só a equipe vê oficinas desativadas (no Nest, a rota pública listava todas).</param>
public sealed record ListEventsQuery(int Page, int Limit, bool OnlyActive, bool IncludeInactive) : IQuery<EventPage>;

public sealed record GetEventBySlugQuery(string Slug, bool IncludeInactive) : IQuery<EventEnvelope>;

public sealed record ListRegistrationsQuery(Guid EventId, int Page, int Limit) : IQuery<RegistrationPage>;

internal sealed class WorkshopQueryHandlers(IWorkshopQueries queries)
    : IQueryHandler<ListEventsQuery, EventPage>,
      IQueryHandler<GetEventBySlugQuery, EventEnvelope>,
      IQueryHandler<ListRegistrationsQuery, RegistrationPage>
{
    public async Task<Result<EventPage>> Handle(ListEventsQuery request, CancellationToken cancellationToken)
    {
        var (items, total) = await queries.ListAsync(request.Page, request.Limit, request.OnlyActive || !request.IncludeInactive, cancellationToken);
        return new EventPage(items, total, request.Page, request.Limit, Pages(total, request.Limit));
    }

    public async Task<Result<EventEnvelope>> Handle(GetEventBySlugQuery request, CancellationToken cancellationToken) =>
        await queries.BySlugAsync(request.Slug, cancellationToken) is { } e && (e.IsActive || request.IncludeInactive)
            ? new EventEnvelope(e)
            : WorkshopErrors.NotFound;

    public async Task<Result<RegistrationPage>> Handle(ListRegistrationsQuery request, CancellationToken cancellationToken)
    {
        var (items, total) = await queries.RegistrationsAsync(request.EventId, request.Page, request.Limit, cancellationToken);
        return new RegistrationPage(items, total, request.Page, request.Limit, Pages(total, request.Limit));
    }

    private static int Pages(int total, int limit) => (int)Math.Ceiling(total / (double)limit);
}
