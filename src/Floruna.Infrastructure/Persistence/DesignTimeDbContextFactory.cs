using Floruna.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Floruna.Infrastructure.Persistence;

/// <summary>
/// Usada só pelo <c>dotnet ef migrations add</c>, que precisa instanciar o
/// DbContext sem subir a API. Não há loja no contexto (nem precisa: criar
/// migration não consulta dados).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FlorunaDbContext>
{
    public FlorunaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FlorunaDbContext>()
            .UseNpgsql("Host=localhost;Database=floruna;Username=floruna;Password=floruna_dev_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new FlorunaDbContext(options, new TenantContext());
    }
}
