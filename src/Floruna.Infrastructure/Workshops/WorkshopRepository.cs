using Floruna.Application.Abstractions;
using Floruna.Application.Workshops;
using Floruna.Domain.Workshops;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Workshops;

internal sealed class WorkshopRepository(FlorunaDbContext db, ITenantContext tenantContext) : IWorkshopRepository
{
    public void Add(Workshop workshop) => db.Workshops.Add(workshop);

    public void Add(WorkshopRegistration registration) => db.WorkshopRegistrations.Add(registration);

    public void Remove(Workshop workshop) => db.Workshops.Remove(workshop);

    public Task<Workshop?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Workshops.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public async Task<Workshop?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Trava de linha exige transação: use IUnitOfWork.ExecuteInTransactionAsync.");
        }

        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM workshops WHERE id = {id} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        return await db.Workshops.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Workshops.AnyAsync(w => w.Slug == slug && w.Id != exceptId, cancellationToken);

    /// <summary>
    /// Um comando só, sem ler antes: a condição (<c>registered &lt; capacity</c>) e o incremento são
    /// avaliados JUNTOS pelo banco, com a linha travada durante o UPDATE. Não há "li 9, gravei 10"
    /// entre duas requisições. O filtro de loja do EF entra no WHERE.
    /// </summary>
    public async Task<bool> TryTakeSeatAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Workshops
            .Where(w => w.Id == id && w.IsActive && w.Registered < w.Capacity)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Registered, w => w.Registered + 1), cancellationToken) == 1;
}

internal sealed class WorkshopQueries(FlorunaDbContext db) : IWorkshopQueries
{
    public async Task<(IReadOnlyList<EventResponse> Items, int Total)> ListAsync(int page, int limit, bool onlyActive, CancellationToken cancellationToken)
    {
        var query = db.Workshops.AsNoTracking().Where(w => !onlyActive || w.IsActive);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(w => w.CreatedAt).Skip((page - 1) * limit).Take(limit).ToListAsync(cancellationToken);
        return ([.. items.Select(EventResponse.From)], total);
    }

    public async Task<EventResponse?> BySlugAsync(string slug, CancellationToken cancellationToken) =>
        await db.Workshops.AsNoTracking().FirstOrDefaultAsync(w => w.Slug == slug, cancellationToken) is { } w ? EventResponse.From(w) : null;

    public async Task<(IReadOnlyList<RegistrationResponse> Items, int Total)> RegistrationsAsync(Guid workshopId, int page, int limit, CancellationToken cancellationToken)
    {
        var query = db.WorkshopRegistrations.AsNoTracking().Where(r => r.WorkshopId == workshopId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(r => r.CreatedAt).Skip((page - 1) * limit).Take(limit).ToListAsync(cancellationToken);
        return ([.. items.Select(r => new RegistrationResponse(r.Id.ToString(), r.WorkshopId.ToString(), r.Name, r.Phone, r.Email,
            Application.Catalog.CatalogMapping.Iso(r.CreatedAt)))], total);
    }
}
