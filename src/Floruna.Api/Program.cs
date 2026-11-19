using Floruna.Api;
using Floruna.Application;
using Floruna.Infrastructure;
using Floruna.Infrastructure.Persistence;

// Composition root: o ÚNICO lugar que conhece todas as camadas.
var builder = WebApplication.CreateBuilder(args);

// Toda linha de log leva o TraceId/SpanId do trace atual: do log se chega ao trace, e vice-versa.
builder.Logging.Configure(options => options.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddPresentation(builder.Configuration);

var app = builder.Build();

// Modo migrator (task avulsa no deploy): aplica as migrations e sai, sem subir HTTP nem jobs.
if (args is ["migrate", ..])
{
    await app.Services.MigrateDatabaseAsync().ConfigureAwait(false);
    return;
}

app.UsePresentation();

await app.Services.InitializeDatabaseAsync().ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposto para o WebApplicationFactory dos testes de integração.</summary>
public partial class Program;
