using Floruna.Domain.Catalog;
using Floruna.SharedKernel;

namespace Floruna.Domain.Workshops;

public sealed record WorkshopSpec(
    string Title, string? Slug, string ShortDescription, string Description, string Date, string Location, string Image,
    long PriceCents, int Capacity, bool IsActive);

/// <summary>
/// Oficina/evento da floricultura (no contrato do Nest: <c>events</c>). <see cref="Registered"/> nunca
/// passa de <see cref="Capacity"/>: a vaga é ocupada por um UPDATE condicional no banco (ver o
/// repositório), e um <c>CHECK</c> na tabela é a segunda barreira.
/// </summary>
public sealed class Workshop : AggregateRoot<Guid>, ITenantOwned
{
    private Workshop()
        : base(Guid.Empty)
    {
        Slug = Title = ShortDescription = Description = Date = Location = Image = string.Empty;
    }

    private Workshop(TenantId tenantId, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Slug = Title = ShortDescription = Description = Date = Location = Image = string.Empty;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public string Slug { get; private set; }

    public string Title { get; private set; }

    public string ShortDescription { get; private set; }

    public string Description { get; private set; }

    /// <summary>Texto livre como no Nest (o front formata); o domínio não interpreta.</summary>
    public string Date { get; private set; }

    public string Location { get; private set; }

    public string Image { get; private set; }

    /// <summary>Centavos; 0 = gratuita.</summary>
    public long PriceCents { get; private set; }

    public int Capacity { get; private set; }

    public int Registered { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int SeatsLeft => Math.Max(0, Capacity - Registered);

    public static Result<Workshop> Create(TenantId tenantId, WorkshopSpec spec, DateTimeOffset now)
    {
        var workshop = new Workshop(tenantId, now);
        var applied = workshop.Apply(spec, now);
        return applied.IsFailure ? applied.Error : workshop;
    }

    /// <summary>Só para testes de domínio: uma oficina que já tem inscrições.</summary>
    public static Workshop FromPersistenceForTests(TenantId tenantId, WorkshopSpec spec, int registered, DateTimeOffset now)
    {
        var workshop = Create(tenantId, spec, now).Value;
        workshop.Registered = registered;
        return workshop;
    }

    public Result Update(WorkshopSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Capacity < Registered)
        {
            return Error.Validation("VALIDATION_ERROR", $"A capacidade não pode ser menor que as {Registered} inscrições já feitas.");
        }

        return Apply(spec with { Slug = string.IsNullOrWhiteSpace(spec.Slug) && spec.Title == Title ? Slug : spec.Slug }, now);
    }

    /// <summary>Apagar uma oficina com inscritos apagaria as inscrições junto: desative em vez disso.</summary>
    public Result EnsureCanBeRemoved() => Registered == 0
        ? Result.Success()
        : Error.Conflict("CONFLICT", "Esta oficina já tem inscrições. Desative-a em vez de remover.");

    private Result Apply(WorkshopSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Capacity < 1)
        {
            return Error.Validation("VALIDATION_ERROR", "A capacidade deve ser de pelo menos 1 vaga.");
        }

        if (spec.PriceCents < 0)
        {
            return Error.Validation("VALIDATION_ERROR", "O preço não pode ser negativo.");
        }

        var slug = CatalogSlug.FromInputOrName(spec.Slug, spec.Title);
        if (slug.IsFailure)
        {
            return slug.Error;
        }

        (Slug, Title, ShortDescription, Description) = (slug.Value.Value, spec.Title.Trim(), spec.ShortDescription.Trim(), spec.Description.Trim());
        (Date, Location, Image, PriceCents, Capacity, IsActive) = (spec.Date.Trim(), spec.Location.Trim(), spec.Image.Trim(), spec.PriceCents, spec.Capacity, spec.IsActive);
        UpdatedAt = now;
        return Result.Success();
    }
}

/// <summary>Inscrição de um participante. Gravada na MESMA transação que ocupa a vaga.</summary>
public sealed class WorkshopRegistration : Entity<Guid>, ITenantOwned
{
    private WorkshopRegistration()
        : base(Guid.Empty)
    {
        Name = Phone = string.Empty;
    }

    public WorkshopRegistration(TenantId tenantId, Guid workshopId, string name, string phone, string? email, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        WorkshopId = workshopId;
        Name = name.Trim();
        Phone = phone.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public Guid WorkshopId { get; private set; }

    public string Name { get; private set; }

    public string Phone { get; private set; }

    public string? Email { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

public static class WorkshopErrors
{
    public static readonly Error NotFound = Error.NotFound("NOT_FOUND", "Evento não encontrado.");
    public static readonly Error SoldOut = Error.Conflict("CONFLICT", "Evento esgotado. Não há mais vagas.");

    public static Error SlugTaken(string slug) => Error.Validation("VALIDATION_ERROR", $"Já existe um evento com o slug \"{slug}\".");
}

public interface IWorkshopRepository
{
    void Add(Workshop workshop);

    void Add(WorkshopRegistration registration);

    void Remove(Workshop workshop);

    Task<Workshop?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Travada: mudar a capacidade espera as inscrições em andamento (e vice-versa).</summary>
    Task<Workshop?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken cancellationToken);

    /// <summary>
    /// Ocupa UMA vaga, atomicamente: <c>UPDATE ... SET registered = registered + 1 WHERE ativa AND
    /// registered &lt; capacity</c>. Concorrentes na última vaga: o banco serializa, só um vence.
    /// </summary>
    Task<bool> TryTakeSeatAsync(Guid id, CancellationToken cancellationToken);
}
