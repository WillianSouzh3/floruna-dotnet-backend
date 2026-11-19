using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Floruna.Application.Payments;
using Floruna.Domain.Payments;
using Floruna.SharedKernel;

namespace Floruna.Infrastructure.Payments;

public sealed class MercadoPagoOptions
{
    public const string SectionName = "MercadoPago";

    public Uri BaseUrl { get; set; } = new("https://api.mercadopago.com/");
}

/// <summary>
/// ADAPTADOR + ANTI-CORRUPTION LAYER do Mercado Pago. Tudo que é "deles" (snake_case,
/// reais com casas decimais, <c>in_process</c>, <c>charged_back</c>) morre aqui; para dentro
/// só saem os tipos do nosso domínio (<see cref="ProviderPaymentStatus"/>, centavos).
/// </summary>
/// <remarks>
/// O <see cref="HttpClient"/> vem do <c>IHttpClientFactory</c> com o pipeline de resiliência
/// padrão (timeout por tentativa, retry com backoff e jitter, circuit breaker). Retentar o POST
/// é seguro porque vai com <c>X-Idempotency-Key</c>: o Mercado Pago não cria o PIX duas vezes.
/// Com o circuito aberto, a chamada falha na hora (fail fast) em vez de empilhar requisições
/// presas num gateway fora do ar, e o checkout cai para o PIX direto (se a loja habilitou).
/// </remarks>
internal sealed class MercadoPagoGateway(HttpClient http) : IPaymentGateway
{
    private static readonly Error Unavailable = Error.Failure("GATEWAY_UNAVAILABLE", "O gateway de pagamento não respondeu.");

    public async Task<Result<GatewayPix>> CreatePixAsync(string accessToken, GatewayPixRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var names = request.CustomerName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/payments")
        {
            Content = JsonContent.Create(new MpCreatePayment(
                request.AmountCents / 100m,
                $"Pedido {request.StoreName}",
                "pix",
                new MpPayer(request.CustomerEmail ?? "cliente@floruna.com.br", names.ElementAtOrDefault(0) ?? "Cliente", names.ElementAtOrDefault(1) ?? "-"),
                request.OrderId.ToString(),
                request.StoreName.Length > 22 ? request.StoreName[..22] : request.StoreName,
                request.NotificationUrl)),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Headers.Add("X-Idempotency-Key", $"order-{request.OrderId}");

        var response = await SendAsync<MpPayment>(message, cancellationToken);
        if (response.IsFailure)
        {
            return response.Error;
        }

        var qr = response.Value.PointOfInteraction?.TransactionData?.QrCode;
        return string.IsNullOrEmpty(qr)
            ? Error.Failure("GATEWAY_INCOMPLETE", "O gateway respondeu sem o código PIX.")
            : new GatewayPix(response.Value.Id.ToString(CultureInfo.InvariantCulture), qr);
    }

    public async Task<Result<GatewayPayment>> GetPaymentAsync(string accessToken, string providerPaymentId, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"v1/payments/{Uri.EscapeDataString(providerPaymentId)}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await SendAsync<MpPayment>(message, cancellationToken);
        if (response.IsFailure)
        {
            return response.Error;
        }

        var p = response.Value;
        return new GatewayPayment(
            p.Id.ToString(CultureInfo.InvariantCulture),
            Translate(p.Status),
            (long)Math.Round(p.TransactionAmount * 100m, MidpointRounding.AwayFromZero),
            Guid.TryParse(p.ExternalReference, out var orderId) ? orderId : null);
    }

    /// <summary>A tradução: o vocabulário do provedor para o nosso.</summary>
    internal static ProviderPaymentStatus Translate(string? status) => status switch
    {
        "approved" => ProviderPaymentStatus.Approved,
        "rejected" => ProviderPaymentStatus.Rejected,
        "cancelled" => ProviderPaymentStatus.Cancelled,
        "refunded" or "charged_back" => ProviderPaymentStatus.Refunded,
        _ => ProviderPaymentStatus.Pending, // pending, in_process, authorized, in_mediation...
    };

    private async Task<Result<T>> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Error.Failure("GATEWAY_ERROR", $"Mercado Pago respondeu {(int)response.StatusCode}.");
            }

            return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Polly.CircuitBreaker.BrokenCircuitException
                                       or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                       or Polly.Timeout.TimeoutRejectedException)
        {
            return Unavailable;
        }
    }

    private sealed record MpCreatePayment(
        [property: JsonPropertyName("transaction_amount")] decimal TransactionAmount,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("payment_method_id")] string PaymentMethodId,
        [property: JsonPropertyName("payer")] MpPayer Payer,
        [property: JsonPropertyName("external_reference")] string ExternalReference,
        [property: JsonPropertyName("statement_descriptor")] string StatementDescriptor,
        [property: JsonPropertyName("notification_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NotificationUrl);

    private sealed record MpPayer(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("first_name")] string FirstName,
        [property: JsonPropertyName("last_name")] string LastName);

    private sealed record MpPayment(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("transaction_amount")] decimal TransactionAmount,
        [property: JsonPropertyName("external_reference")] string? ExternalReference,
        [property: JsonPropertyName("point_of_interaction")] MpPointOfInteraction? PointOfInteraction);

    private sealed record MpPointOfInteraction([property: JsonPropertyName("transaction_data")] MpTransactionData? TransactionData);

    private sealed record MpTransactionData([property: JsonPropertyName("qr_code")] string? QrCode);
}
