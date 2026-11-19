using Floruna.Application;
using Floruna.Application.Abstractions;
using Floruna.Application.Payments;
using Floruna.Infrastructure;
using Floruna.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Floruna.Migration;

/// <summary>
/// Migração de UMA loja do Nest (Mongo) para a API .NET (PostgreSQL). Ver docs/migracao.md.
/// <code>dotnet Floruna.Migration.dll --mongo "mongodb://..." --mongo-db flora-lis --tenant flora-lis [--dry-run] [--replace] [--force]</code>
/// Destino e segredos vêm das MESMAS variáveis da API: ConnectionStrings__Floruna (ou Database__*),
/// Secrets__EncryptionKey (recifra o token do gateway) e, opcional, NEST_SETTINGS_ENCRYPTION_KEY.
/// </summary>
internal static class MigrationCli
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliArgs.Parse(args);
        if (options is null)
        {
            await Console.Error.WriteLineAsync("Uso: --mongo <uri> --mongo-db <banco> --tenant <slug> [--dry-run] [--replace] [--force]");
            return 2;
        }

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
            .AddMetrics()
            .AddSingleton<IConfiguration>(configuration)
            .AddApplication()
            .AddInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var nestKey = configuration["NEST_SETTINGS_ENCRYPTION_KEY"] is { Length: > 0 } key ? Convert.FromBase64String(key) : null;
        var mongo = new MongoClient(options.MongoUri).GetDatabase(options.MongoDatabase);
        var migrator = new NestTenantMigrator(mongo, scope.ServiceProvider.GetRequiredService<FlorunaDbContext>(),
            scope.ServiceProvider.GetRequiredService<TenantContext>(), scope.ServiceProvider.GetRequiredService<ISecretProtector>(), TimeProvider.System);

        var report = await migrator.MigrateAsync(new MigrationRequest(options.Tenant, options.DryRun, options.Replace, options.Force, nestKey), CancellationToken.None);
        Console.WriteLine(ReportFormatter.Format(report));
        return report.Committed || (options.DryRun && report.Blockers.Count == 0) ? 0 : 1;
    }

    private sealed record CliArgs(string MongoUri, string MongoDatabase, string Tenant, bool DryRun, bool Replace, bool Force)
    {
        public static CliArgs? Parse(string[] args)
        {
            string? Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            return Value("--mongo") is { } uri && Value("--mongo-db") is { } database && Value("--tenant") is { } tenant
                ? new CliArgs(uri, database, tenant, args.Contains("--dry-run"), args.Contains("--replace"), args.Contains("--force"))
                : null;
        }
    }
}
