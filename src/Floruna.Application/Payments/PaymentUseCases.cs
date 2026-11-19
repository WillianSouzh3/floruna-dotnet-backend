using System.Text.RegularExpressions;
using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Application.Outbox;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Floruna.Application.Payments;

// ── Respostas ────────────────────────────────────────────────────────────

public sealed record TransactionResponse(string Status, string Source, string ActorType, string? ExternalId, PaymentEvidence? Evidence, long Amount, string ObservedAt);

public sealed record PaymentIntentResponse(
    string Id, string OrderId, string Status, string Rail, string? ProviderPaymentId, long Amount, string Currency, long PaidAmount,
    string? PaidAt, bool FallbackUsed, string? ReconciliationIssue, string CreatedAt)
{
    public static PaymentIntentResponse From(PaymentIntent i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.Id.ToString(), i.OrderId.ToString(), PaymentContract.Status(i.Status), PaymentContract.Rail(i.Rail), i.ProviderPaymentId,
            i.AmountCents, i.Currency, i.PaidAmountCents, i.PaidAt is { } p ? CatalogMapping.Iso(p) : null, i.FallbackUsed, i.ReconciliationIssue,
            CatalogMapping.Iso(i.CreatedAt));
    }
}

public sealed record PaymentIntentEnvelope(PaymentIntentResponse PaymentIntent, IReadOnlyList<TransactionResponse> Transactions)
{
    public static PaymentIntentEnvelope From(PaymentIntent i) => new(PaymentIntentResponse.From(i),
        [.. i.Transactions.Select(t => new TransactionResponse(t.Status.ToString().ToLowerInvariant(), t.Source.ToString().ToLowerInvariant(),
            t.ActorType, t.ExternalId, t.Evidence, t.AmountCents, CatalogMapping.Iso(t.ObservedAt)))]);
}

/// <summary>Configuração de pagamento SEM o token (só se ele está configurado).</summary>
public sealed record PaymentSettingsResponse(string PaymentGateway, bool MercadoPagoConfigured, bool MercadoPagoOfflineFallbackEnabled, string PixKey, string PixReceiverName, string PixCity);

// ── Configuração ─────────────────────────────────────────────────────────

public sealed record GetPaymentSettingsQuery : IQuery<PaymentSettingsResponse>;

/// <param name="MercadoPagoAccessToken">Null = manter o atual; vazio = remover.</param>
public sealed record UpdatePaymentSettingsCommand(
    string? PaymentGateway, bool? OfflineFallbackEnabled, string? PixKey, string? PixReceiverName, string? PixCity, string? MercadoPagoAccessToken)
    : ICommand<PaymentSettingsResponse>;

internal sealed class UpdatePaymentSettingsValidator : AbstractValidator<UpdatePaymentSettingsCommand>
{
    public UpdatePaymentSettingsValidator()
    {
        RuleFor(x => x.PaymentGateway).Must(g => g is null or "none" or "mercadopago").WithMessage("Gateway inválido (none ou mercadopago).");
        RuleFor(x => x.PixKey).MaximumLength(77).WithMessage("A chave Pix deve ter até 77 caracteres.");
        RuleFor(x => x.PixReceiverName).MaximumLength(60);
        RuleFor(x => x.PixCity).MaximumLength(60);
        RuleFor(x => x.MercadoPagoAccessToken).MaximumLength(300);
    }
}

internal sealed class PaymentSettingsHandlers(ITenantContext tenantContext, IPaymentRepository payments, ISecretProtector secrets, IUnitOfWork unitOfWork)
    : IQueryHandler<GetPaymentSettingsQuery, PaymentSettingsResponse>,
      ICommandHandler<UpdatePaymentSettingsCommand, PaymentSettingsResponse>
{
    public async Task<Result<PaymentSettingsResponse>> Handle(GetPaymentSettingsQuery request, CancellationToken cancellationToken) =>
        ToResponse(await payments.GetSettingsAsync(cancellationToken));

    public async Task<Result<PaymentSettingsResponse>> Handle(UpdatePaymentSettingsCommand request, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Tenant!;
        var settings = await payments.GetSettingsAsync(cancellationToken);
        if (settings is null)
        {
            settings = PaymentSettings.For(tenant.Id);
            payments.Add(settings);
        }

        var useGateway = request.PaymentGateway is null ? settings.UseMercadoPago : request.PaymentGateway == "mercadopago";
        if (useGateway && !PlanCatalog.For(tenant.EffectivePlan).Allows(PlanFeature.PaymentGateway))
        {
            return PaymentErrors.GatewayNotAllowed;
        }

        settings.Update(useGateway, request.OfflineFallbackEnabled ?? settings.OfflineFallbackEnabled,
            request.PixKey ?? settings.PixKey, request.PixReceiverName ?? settings.PixReceiverName, request.PixCity ?? settings.PixCity);
        if (request.MercadoPagoAccessToken is { } token)
        {
            settings.SetMercadoPagoToken(token.Length == 0 ? null : secrets.Protect(token.Trim(), SecretContexts.MercadoPagoToken(tenant.Id)));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToResponse(settings);
    }

    private static PaymentSettingsResponse ToResponse(PaymentSettings? s) => new(
        s?.UseMercadoPago == true ? "mercadopago" : "none", s?.MercadoPagoTokenCiphertext is not null, s?.OfflineFallbackEnabled ?? false,
        s?.PixKey ?? string.Empty, s?.PixReceiverName ?? string.Empty, s?.PixCity ?? string.Empty);
}

// ── Webhook do gateway ───────────────────────────────────────────────────

/// <summary>Aviso do Mercado Pago. Só o ID é usado; o resto do corpo não é confiável.</summary>
public sealed record ReceivePaymentWebhookCommand(string? TenantSlug, string? Type, string? Action, string? PaymentId) : ICommand;

internal sealed partial class ReceivePaymentWebhookHandler(
    TenantContext tenantContext, ITenantResolver resolver, IPaymentRepository payments, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ReceivePaymentWebhookCommand>
{
    public async Task<Result> Handle(ReceivePaymentWebhookCommand request, CancellationToken cancellationToken)
    {
        // Responde OK mesmo ao que ignora: erro faria o gateway insistir sem motivo.
        if (request.Type != "payment" && request.Action != "payment.updated")
        {
            return Result.Success();
        }

        // Alfabeto conservador: limita o tamanho da inbox e impede injeção em log/URL.
        if (request.PaymentId is null || !SafeId().IsMatch(request.PaymentId) ||
            request.TenantSlug is null || await resolver.ResolveBySlugAsync(request.TenantSlug, cancellationToken) is not { } tenant)
        {
            return Result.Success();
        }

        // Grava o aviso + evento na outbox (mesma transação). O processamento (consultar o
        // gateway, confirmar o pedido) acontece fora do request, com retry e fila de mortas.
        using (tenantContext.Enter(tenant))
        {
            payments.Add(PaymentNotification.Receive(tenant.Id, "mercadopago", request.PaymentId, clock.GetUtcNow()));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}

/// <summary>
/// Processa o aviso VERIFICANDO NA FONTE (verify-by-fetch): busca o pagamento no Mercado
/// Pago com o token da loja. Um webhook forjado só faria o sistema consultar um pagamento
/// real; nunca aprovaria um pedido por conta própria.
/// </summary>
internal sealed partial class PaymentNotificationHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    ISecretProtector secrets,
    PaymentConfirmation confirmation,
    ITenantContext tenantContext,
    ILogger<PaymentNotificationHandler> logger)
    : INotificationHandler<DomainEventNotification<PaymentNotificationReceived>>
{
    public async Task Handle(DomainEventNotification<PaymentNotificationReceived> notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var paymentId = notification.Event.ProviderPaymentId;
        var settings = await payments.GetSettingsAsync(cancellationToken);
        var token = settings?.MercadoPagoTokenCiphertext is { } cipher
            ? secrets.Unprotect(cipher, SecretContexts.MercadoPagoToken(tenantContext.RequiredTenantId))
            : null;
        if (token is null)
        {
            LogIgnored(logger, paymentId, "loja sem token do Mercado Pago");
            return;
        }

        var fetched = await gateway.GetPaymentAsync(token, paymentId, cancellationToken);
        if (fetched.IsFailure)
        {
            // Exceção = a outbox tenta de novo com backoff (gateway instável, rede).
            throw new InvalidOperationException($"Não foi possível consultar o pagamento {paymentId}: {fetched.Error.Message}");
        }

        var payment = fetched.Value;
        var intent = await payments.FindByProviderPaymentIdAsync(paymentId, cancellationToken)
            ?? (payment.OrderId is { } orderId ? await payments.FindByOrderAsync(new OrderId(orderId), cancellationToken) : null);
        if (intent is null)
        {
            LogIgnored(logger, paymentId, "nenhuma intenção corresponde");
            return;
        }

        var result = await confirmation.ApplyAsync(intent.Id,
            i => i.RecordProviderStatus(payment.Status, payment.AmountCents, $"mercadopago:{paymentId}:{payment.Status}", notification.Event.OccurredAt),
            cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Aviso de pagamento {PaymentId} ignorado: {Reason}")]
    private static partial void LogIgnored(ILogger logger, string paymentId, string reason);
}

// ── Confirmação manual e consultas ───────────────────────────────────────

public sealed record ConfirmManualPaymentCommand(
    Guid IntentId, long Amount, string? ExternalId, PaymentEvidence Evidence, DateTimeOffset? ReceivedAt, string IdempotencyKey, Guid? ActorId)
    : ICommand<PaymentIntentEnvelope>;

internal sealed partial class ConfirmManualPaymentValidator : AbstractValidator<ConfirmManualPaymentCommand>
{
    public ConfirmManualPaymentValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("Informe o valor recebido.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(120).WithMessage("idempotencyKey é obrigatória.");
        RuleFor(x => x.Evidence.Type).Must(t => t is "pix_receipt" or "bank_statement" or "other").WithMessage("Tipo de evidência inválido.");
        RuleFor(x => x.Evidence.Reference).MaximumLength(80).Matches(SafeReference()).WithMessage("Referência de evidência inválida.");
        RuleFor(x => x.Evidence.Note).MaximumLength(300);
    }

    [GeneratedRegex("^[A-Za-z0-9._:/-]*$")]
    private static partial Regex SafeReference();
}

internal sealed class ManualPaymentHandler(PaymentConfirmation confirmation, TimeProvider clock)
    : ICommandHandler<ConfirmManualPaymentCommand, PaymentIntentEnvelope>
{
    public async Task<Result<PaymentIntentEnvelope>> Handle(ConfirmManualPaymentCommand request, CancellationToken cancellationToken)
    {
        var result = await confirmation.ApplyAsync(request.IntentId,
            i => i.ConfirmManually(request.Amount, request.Evidence, request.ExternalId, request.ActorId, request.IdempotencyKey, request.ReceivedAt ?? clock.GetUtcNow()),
            cancellationToken);
        return result.IsFailure ? result.Error : PaymentIntentEnvelope.From(result.Value);
    }
}

public sealed record GetPaymentIntentQuery(Guid Id) : IQuery<PaymentIntentEnvelope>;

public sealed record GetOrderPaymentQuery(Guid OrderId) : IQuery<PaymentIntentEnvelope>;

internal sealed class PaymentQueryHandlers(IPaymentRepository payments)
    : IQueryHandler<GetPaymentIntentQuery, PaymentIntentEnvelope>,
      IQueryHandler<GetOrderPaymentQuery, PaymentIntentEnvelope>
{
    public async Task<Result<PaymentIntentEnvelope>> Handle(GetPaymentIntentQuery request, CancellationToken cancellationToken) =>
        await payments.GetAsync(request.Id, cancellationToken) is { } intent ? PaymentIntentEnvelope.From(intent) : PaymentErrors.IntentNotFound;

    public async Task<Result<PaymentIntentEnvelope>> Handle(GetOrderPaymentQuery request, CancellationToken cancellationToken) =>
        await payments.FindByOrderAsync(new OrderId(request.OrderId), cancellationToken) is { } intent ? PaymentIntentEnvelope.From(intent) : PaymentErrors.IntentNotFound;
}
