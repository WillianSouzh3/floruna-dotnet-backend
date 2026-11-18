using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Floruna.Application.Abstractions;
using Floruna.Infrastructure.Mail;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests.Support;

/// <summary>Captura os e-mails em vez de mandar por SMTP. "Configurado" = 2FA e reset ligados.</summary>
public sealed partial class CapturingEmailTransport : IEmailTransport
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool IsConfigured => true;

    public Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>O envio é assíncrono (fila em background): espera o e-mail chegar.</summary>
    public async Task<EmailMessage> WaitForAsync(string to, Func<EmailMessage, bool>? match = null)
    {
        for (var i = 0; i < 100; i++)
        {
            var found = _sent.LastOrDefault(m => m.To == to && (match?.Invoke(m) ?? true));
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Nenhum e-mail para {to}.");
    }

    public bool AnySentTo(string to) => _sent.Any(m => m.To == to);

    public int CountTo(string to) => _sent.Count(m => m.To == to);

    public static string CodeIn(EmailMessage message) => SixDigits().Match(message.TextBody).Value;

    public static string ResetTokenIn(EmailMessage message) => ResetToken().Match(message.TextBody).Groups[1].Value;

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();

    [GeneratedRegex("token=([0-9a-f]+)")]
    private static partial Regex ResetToken();
}

/// <summary>DNS de mentira: os testes publicam o TXT que quiserem.</summary>
public sealed class FakeDnsTxtResolver : IDnsTxtResolver
{
    private readonly ConcurrentDictionary<string, string[]> _records = new();

    public void Publish(string name, params string[] values) => _records[name] = values;

    public Task<TxtLookup> ResolveAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryGetValue(name, out var values)
            ? new TxtLookup(TxtLookupStatus.Found, values)
            : new TxtLookup(TxtLookupStatus.NotFound, []));
}

public static class FactoryVariants
{
    /// <summary>A mesma API, com e-mail "ligado" (logo, com 2FA no login da equipe).</summary>
    public static WebApplicationFactory<Program> WithEmail(this FlorunaApiFactory factory, CapturingEmailTransport transport) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IEmailTransport>(transport)));

    public static WebApplicationFactory<Program> WithDns(this FlorunaApiFactory factory, FakeDnsTxtResolver dns) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IDnsTxtResolver>(dns)));

    public static HttpClient ForStore(this WebApplicationFactory<Program> factory, string slug, string? token = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-tenant-host", $"{slug}.localhost:3000");
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }
}
