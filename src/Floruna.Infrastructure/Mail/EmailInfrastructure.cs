using System.Threading.Channels;
using Floruna.Application.Abstractions;
using MailKit; // MailKit 5: Send(message) virou método de extensão
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Floruna.Infrastructure.Mail;

public sealed class SmtpSettings
{
    public const string SectionName = "Smtp";

    /// <summary>Sem host, o e-mail fica DESLIGADO (e o 2FA também). Na AWS: SES via SMTP.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "Floruna <nao-responda@floruna.com.br>";
}

/// <summary>Quem efetivamente entrega a mensagem (SMTP). Separado da fila para ser trocado em testes.</summary>
public interface IEmailTransport
{
    bool IsConfigured { get; }

    Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken);
}

internal sealed class SmtpEmailTransport(IOptions<SmtpSettings> settings) : IEmailTransport
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.Host);

    public async Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var smtp = settings.Value;
        using var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(smtp.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host ?? string.Empty, smtp.Port, SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.Username))
        {
            await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

/// <summary>
/// Padrão de nuvem "Queue-Based Load Leveling": a requisição só ENFILEIRA o
/// e-mail (microssegundos) e um <see cref="BackgroundService"/> envia depois.
/// </summary>
/// <remarks>
/// <para>Ganhos: o login com 2FA não espera o SMTP; e o "esqueci minha senha"
/// responde no mesmo tempo com ou sem conta (o envio não vaza pelo tempo).</para>
/// <para>Limite consciente: a fila é em memória e se perde se o processo cair.
/// Na Fase 4 o envio passa pela outbox (durável).</para>
/// </remarks>
internal sealed class BackgroundEmailQueue(IEmailTransport transport) : IEmailSender
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public bool IsEnabled => transport.IsConfigured;

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public ValueTask SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(message, cancellationToken);
}

internal sealed partial class EmailDispatcher(BackgroundEmailQueue queue, IEmailTransport transport, ILogger<EmailDispatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await transport.DeliverAsync(message, stoppingToken);
            }
#pragma warning disable CA1031 // um e-mail que falha não pode derrubar o despachante
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex, message.Subject);
            }
        }
    }

    // Nunca logar o destinatário nem o corpo (podem conter link de reset e PII).
    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao enviar e-mail \"{Subject}\"")]
    private static partial void LogFailed(ILogger logger, Exception exception, string subject);
}

/// <summary>Entrega SÍNCRONA (para os handlers da outbox): a exceção do SMTP volta para a outbox, que repete.</summary>
internal sealed class SmtpEmailDelivery(IEmailTransport transport) : IEmailDelivery
{
    public bool IsEnabled => transport.IsConfigured;

    public Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken) => transport.DeliverAsync(message, cancellationToken);
}
