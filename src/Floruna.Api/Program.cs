using Floruna.Api;
using Floruna.Application;
using Floruna.Infrastructure;

// Composition root: o ÚNICO lugar que conhece todas as camadas.
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddPresentation(builder.Configuration);

var app = builder.Build();

app.UsePresentation();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposto para o WebApplicationFactory dos testes de integração.</summary>
public partial class Program;
