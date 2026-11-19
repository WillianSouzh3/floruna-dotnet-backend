using System.Text.Json;
using Floruna.Application.Messaging;
using Floruna.Application.Payments;
using Floruna.Domain.Identity;
using Floruna.Domain.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.Controllers;

public sealed record PaymentSettingsRequest(
    string? PaymentGateway, bool? MercadoPagoOfflineFallbackEnabled, string? PixKey, string? PixReceiverName, string? PixCity, string? MercadoPagoAccessToken);

/// <summary>O Mercado Pago manda o id como número; aceitamos número ou texto.</summary>
public sealed record WebhookData(JsonElement Id);

public sealed record WebhookBody(string? Type, string? Action, WebhookData? Data);

public sealed record EvidenceRequest(string? Type, string? Reference, string? Note);

public sealed record ConfirmManualRequest(long? Amount, string? ExternalId, EvidenceRequest? Evidence, DateTimeOffset? ReceivedAt, string? IdempotencyKey);

/// <summary>
/// Configuração de pagamento da loja. Desvio consciente do Nest (que tem um <c>/settings</c> único):
/// aqui é um recurso próprio, e o token do gateway é SÓ ESCRITA (a resposta diz se está configurado).
/// </summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/settings/payments")]
[Authorize(Roles = Roles.Admins)]
public sealed class PaymentSettingsController(ISender sender) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetPaymentSettingsQuery(), cancellationToken), Ok);

    [HttpPut]
    public async Task<IActionResult> Update(PaymentSettingsRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdatePaymentSettingsCommand(body.PaymentGateway, body.MercadoPagoOfflineFallbackEnabled,
            body.PixKey, body.PixReceiverName, body.PixCity, body.MercadoPagoAccessToken), cancellationToken), Ok);
    }
}

[Route(Contracts.ApiVersion.RoutePrefix + "/payments")]
public sealed class PaymentsController(ISender sender) : ApiController
{
    /// <summary>
    /// Webhook do Mercado Pago. SEMPRE 200: o aviso é gravado e processado depois (outbox).
    /// Responder erro faria o gateway insistir; e o conteúdo nem é confiável (verify-by-fetch).
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook([FromQuery] string? tenant, [FromBody] WebhookBody? body, CancellationToken cancellationToken)
    {
        var paymentId = body?.Data?.Id.ValueKind switch
        {
            JsonValueKind.Number => body.Data.Id.GetRawText(),
            JsonValueKind.String => body.Data.Id.GetString(),
            _ => null,
        };
        await sender.Send(new ReceivePaymentWebhookCommand(tenant, body?.Type, body?.Action, paymentId), cancellationToken);
        return Ok(new { ok = true });
    }

    [HttpGet("intents")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> ByOrder([FromQuery] Guid orderId, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetOrderPaymentQuery(orderId), cancellationToken), Ok);

    [HttpGet("intents/{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetPaymentIntentQuery(id), cancellationToken), Ok);

    /// <summary>A loja confirma que o PIX caiu na conta (valor exato + comprovante; auditado).</summary>
    [HttpPost("intents/{id:guid}/confirm-manual")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> ConfirmManual(Guid id, ConfirmManualRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var evidence = new PaymentEvidence(body.Evidence?.Type ?? string.Empty, body.Evidence?.Reference ?? string.Empty, body.Evidence?.Note ?? string.Empty);
        var command = new ConfirmManualPaymentCommand(id, body.Amount ?? 0, body.ExternalId, evidence, body.ReceivedAt, body.IdempotencyKey ?? string.Empty, CurrentActor().UserId);
        return FromResult(await sender.Send(command, cancellationToken), Ok);
    }
}
