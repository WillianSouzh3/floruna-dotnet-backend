using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Application.Ordering;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Floruna.Application.Payments;

/// <summary>
/// Prepara a cobrança de um pedido recém-criado. Roda DEPOIS do commit do checkout:
/// nunca segure uma transação esperando uma chamada de rede (RN-42).
/// </summary>
/// <remarks>
/// Escolha do trilho (Strategy decidida pela configuração da loja + plano):
/// gateway (Mercado Pago) se configurado e permitido; senão BR Code na chave da loja.
/// Se o gateway cair e a loja habilitou contingência, cai para o PIX direto.
/// Falha sem contingência: o pedido existe, mas sem cobrança (<c>paymentInitializationStatus: failed</c>).
/// </remarks>
public sealed partial class PaymentInitializer(
    ITenantContext tenantContext,
    IPaymentRepository payments,
    IOrderRepository orders,
    IPaymentGateway gateway,
    ISecretProtector secrets,
    IUnitOfWork unitOfWork,
    IOptions<PlatformSettings> platform,
    TimeProvider clock,
    ILogger<PaymentInitializer> logger)
{
    public async Task InitializeAsync(OrderId orderId, CancellationToken cancellationToken)
    {
        if (await payments.FindByOrderAsync(orderId, cancellationToken) is not null)
        {
            return; // já preparado (checkout repetido com a mesma chave)
        }

        var order = await orders.GetAsync(orderId, cancellationToken);
        var tenant = tenantContext.Tenant;
        if (order is null || tenant is null)
        {
            return;
        }

        var settings = await payments.GetSettingsAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var prepared = await PrepareAsync(order, tenant, settings, cancellationToken);
        if (prepared is null)
        {
            order.PaymentInitializationFailed(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var (rail, pixCode, providerId, fallback) = prepared.Value;
        payments.Add(PaymentIntent.Open(order.TenantId, order.Id, order.TotalCents, rail, pixCode, providerId, fallback, now));
        order.PaymentReady(PaymentContract.Rail(rail), pixCode, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<(PaymentRail Rail, string PixCode, string? ProviderId, bool Fallback)?> PrepareAsync(
        Order order, ResolvedTenant tenant, PaymentSettings? settings, CancellationToken cancellationToken)
    {
        var gatewayAllowed = PlanCatalog.For(tenant.EffectivePlan).Allows(PlanFeature.PaymentGateway);
        if (settings is { UseMercadoPago: true, MercadoPagoTokenCiphertext: { } cipher } && gatewayAllowed)
        {
            var token = secrets.Unprotect(cipher, SecretContexts.MercadoPagoToken(order.TenantId));
            if (token is not null)
            {
                var notificationUrl = string.IsNullOrEmpty(platform.Value.ApiPublicUrl)
                    ? null
                    : $"{platform.Value.ApiPublicUrl.TrimEnd('/')}/api/v1/payments/webhook?tenant={tenant.Slug}";
                var created = await gateway.CreatePixAsync(token, new GatewayPixRequest(
                    order.TotalCents, order.Id.Value, order.Customer.Name, order.Customer.Email, tenant.Slug, notificationUrl), cancellationToken);
                if (created.IsSuccess)
                {
                    return (PaymentRail.MercadoPago, created.Value.PixCode, created.Value.ProviderPaymentId, false);
                }

                LogGatewayFailed(logger, order.Id.Value, created.Error.Message);
                if (!settings.OfflineFallbackEnabled)
                {
                    return null;
                }
            }
        }

        // PIX direto na chave da loja (sem gateway): BR Code gerado aqui mesmo.
        if (settings is null || settings.PixKey.Length == 0)
        {
            return null;
        }

        var code = PixBrCode.Build(new PixCharge(order.TotalCents, order.Id.Value.ToString("N"), settings.PixKey,
            settings.PixReceiverName.Length > 0 ? settings.PixReceiverName : tenant.Slug, settings.PixCity.Length > 0 ? settings.PixCity : "BRASIL"));
        return code.IsSuccess ? (PaymentRail.PixOffline, code.Value, null, settings.UseMercadoPago) : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway falhou ao criar o PIX do pedido {OrderId}: {Reason}")]
    private static partial void LogGatewayFailed(ILogger logger, Guid orderId, string reason);
}

/// <summary>
/// Aplica um fato de pagamento (do webhook ou da loja) e confirma o pedido, em DOIS passos
/// idempotentes. Por que não uma transação só com tudo? Porque a confirmação do pedido pode
/// falhar por regra (estoque acabou, pedido já foi cancelado por abandono), e o DINHEIRO
/// recebido não pode sumir junto com o rollback:
/// <list type="number">
/// <item>Registra o fato e tenta confirmar o pedido NA MESMA transação (comando de status com
/// chave fixa <c>payment:{intenção}</c>: repetir é no-op).</item>
/// <item>Se a confirmação falhar, registra o pagamento de novo SEM o pedido, marcado com
/// problema de reconciliação para a loja resolver (reembolso).</item>
/// </list>
/// Se o processo cair no meio, a outbox (webhook) ou a loja (manual) repete; cada passo é idempotente.
/// </summary>
public sealed partial class PaymentConfirmation(
    IPaymentRepository payments,
    IOrderRepository orders,
    ISender sender,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<PaymentConfirmation> logger)
{
    public async Task<Result<PaymentIntent>> ApplyAsync(Guid intentId, Func<PaymentIntent, Result<bool>> record, CancellationToken cancellationToken)
    {
        var intent = await payments.GetAsync(intentId, cancellationToken);
        if (intent is null)
        {
            return PaymentErrors.IntentNotFound;
        }

        var became = record(intent);
        if (became.IsFailure)
        {
            return became.Error;
        }

        var order = await orders.GetAsync(intent.OrderId, cancellationToken);
        var now = clock.GetUtcNow();
        if (!became.Value)
        {
            order?.PaymentStatusChanged(ToOrderPaymentStatus(intent.Status), now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return intent;
        }

        // Passo 1: a mudança da intenção (em memória) e a confirmação do pedido vão no MESMO commit.
        order?.PaymentStatusChanged("approved", now);
        var confirmed = await sender.Send(new ChangeOrderStatusCommand(
            intent.OrderId.Value, "confirmado", "pendente", $"payment:{intent.Id}", "Pagamento confirmado", null, "system"), cancellationToken);
        if (confirmed.IsSuccess)
        {
            return intent;
        }

        // Passo 2: o pedido não pôde ser confirmado. O ExecuteInTransaction já desfez tudo e limpou
        // a memória; registra o pagamento sozinho, sinalizado para reconciliação.
        LogReconciliation(logger, intent.Id, confirmed.Error.Message);
        intent = (await payments.GetAsync(intentId, cancellationToken))!;
        record(intent);
        intent.FlagReconciliationIssue($"order_not_confirmed: {confirmed.Error.Message}");
        (await orders.GetAsync(intent.OrderId, cancellationToken))?.PaymentStatusChanged("approved", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return intent;
    }

    private static string ToOrderPaymentStatus(PaymentIntentStatus status) => status switch
    {
        PaymentIntentStatus.Paid => "approved",
        PaymentIntentStatus.Failed => "rejected",
        PaymentIntentStatus.Cancelled => "cancelled",
        PaymentIntentStatus.Refunded => "refunded",
        _ => "pending",
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Pagamento {IntentId} recebido, mas o pedido não pôde ser confirmado: {Reason}. Marcado para reconciliação.")]
    private static partial void LogReconciliation(ILogger logger, Guid intentId, string reason);
}
