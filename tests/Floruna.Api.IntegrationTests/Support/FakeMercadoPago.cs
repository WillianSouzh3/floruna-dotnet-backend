using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Application.Payments;
using Floruna.Infrastructure.Payments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests.Support;

/// <summary>
/// Mercado Pago de mentira no nível HTTP: o ADAPTADOR de verdade (com o pipeline de
/// resiliência) conversa com ele. Testa serialização, tradução de status e falhas de rede.
/// </summary>
public sealed class FakeMercadoPago : HttpMessageHandler
{
    private readonly ConcurrentDictionary<long, (string Status, decimal Amount, string ExternalReference)> _payments = new();
    private long _nextId = 1_000;

    private readonly ConcurrentDictionary<string, long> _refundsByKey = new();
    private long _refundedCents;

    public bool Down { get; set; }

    /// <summary>Recusa reembolsos com 400 (definitivo).</summary>
    public bool RejectRefunds { get; set; }

    /// <summary>Processa o reembolso mas a resposta "se perde" (504): o pior caso, resultado desconhecido.</summary>
    public bool LoseRefundResponses { get; set; }

    /// <summary>Reembolsos EFETIVAMENTE executados (uma vez por chave de idempotência).</summary>
    public int RefundsExecuted => _refundsByKey.Count;

    public long RefundedCents => Interlocked.Read(ref _refundedCents);

    public int Calls;

    public string? LastNotificationUrl { get; private set; }

    public string? LastAuthorization { get; private set; }

    public void SetStatus(string paymentId, string status)
    {
        var id = long.Parse(paymentId, System.Globalization.CultureInfo.InvariantCulture);
        _payments[id] = _payments[id] with { Status = status };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        LastAuthorization = request.Headers.Authorization?.ToString();
        if (Down)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/v1/payments")
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            LastNotificationUrl = root.TryGetProperty("notification_url", out var url) ? url.GetString() : null;
            var id = Interlocked.Increment(ref _nextId);
            _payments[id] = ("pending", root.GetProperty("transaction_amount").GetDecimal(), root.GetProperty("external_reference").GetString()!);
            return Json(new { id, status = "pending", point_of_interaction = new { transaction_data = new { qr_code = $"00020101MP{id}" } } });
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/refunds", StringComparison.Ordinal))
        {
            if (RejectRefunds)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var key = request.Headers.GetValues("X-Idempotency-Key").Single();
            var amountCents = (long)(body.RootElement.GetProperty("amount").GetDecimal() * 100);
            var refundId = _refundsByKey.GetOrAdd(key, _ =>
            {
                Interlocked.Add(ref _refundedCents, amountCents); // mesma chave: o MP devolve o mesmo reembolso, sem pagar de novo
                return Interlocked.Increment(ref _nextId);
            });
            return LoseRefundResponses ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout) : Json(new { id = refundId, status = "approved" });
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/payments/", StringComparison.Ordinal)
            && long.TryParse(path["/v1/payments/".Length..], out var paymentId) && _payments.TryGetValue(paymentId, out var payment))
        {
            return Json(new { id = paymentId, status = payment.Status, transaction_amount = payment.Amount, external_reference = payment.ExternalReference });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

public static class MercadoPagoVariant
{
    public static WebApplicationFactory<Program> WithMercadoPago(this FlorunaApiFactory factory, FakeMercadoPago mp) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<IPaymentGateway, MercadoPagoGateway>().ConfigurePrimaryHttpMessageHandler(() => mp)));
}
