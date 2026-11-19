using System.Text.RegularExpressions;

namespace Floruna.ArchitectureTests;

/// <summary>
/// Regras que o NetArchTest não enxerga (chamadas de método), verificadas no
/// código-fonte. RNF-14: o tempo vem SEMPRE do TimeProvider injetado.
/// </summary>
public sealed partial class SourceRulesTests
{
    [Fact]
    public void Production_code_never_reads_the_system_clock_directly()
    {
        var files = SourceFiles().ToList();
        files.Count.ShouldBeGreaterThan(50, "a varredura precisa encontrar o código-fonte (senão o teste passa sem verificar nada)");

        var offenders = files
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, number: index + 1)))
            .Where(x => SystemClock().IsMatch(x.line) && !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.number}")
            .ToList();

        offenders.ShouldBeEmpty("Use TimeProvider (injetado) em vez de DateTime.Now/UtcNow.");
    }

    /// <summary>
    /// Escapar do filtro da loja (<c>IgnoreQueryFilters</c>) é poder de plataforma.
    /// Cada uso precisa estar nesta lista, revisada em code review.
    /// </summary>
    private static readonly HashSet<string> TenantFilterEscapeAllowlist = new(StringComparer.Ordinal)
    {
        "FlorunaDbContext.cs",   // só cita no comentário do filtro
        "Repositories.cs",       // sessão do superadmin e impersonação (loja explícita na consulta)
        "InventoryQueries.cs",   // job de expiração de reservas (varre todas as lojas)
        "CommerceRepositories.cs", // job de pedidos abandonados (varre todas as lojas)
        "LoyaltyRepository.cs",  // job de expiração de cashback (varre todas as lojas)
        "RefundRepository.cs",   // job de retentativa de reembolsos (varre todas as lojas)
        "CustomOrderRepository.cs", // job de expiração de orçamentos (varre todas as lojas)
        "SubstitutionRepository.cs", // job de expiração de propostas de troca (varre todas as lojas)
    };

    [Fact]
    public void Tenant_filter_is_only_bypassed_in_reviewed_places()
    {
        var offenders = SourceFiles()
            .Where(file => File.ReadAllText(file).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => !TenantFilterEscapeAllowlist.Contains(name!))
            .ToList();

        offenders.ShouldBeEmpty("Uso novo de IgnoreQueryFilters: justifique e inclua na allowlist (ver ADR 0002).");
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Floruna.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull("raiz do repositório não encontrada");
        return Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\bDateTime(Offset)?\.(UtcNow|Now|Today)\b")]
    private static partial Regex SystemClock();
}
