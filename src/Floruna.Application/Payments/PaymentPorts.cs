using Floruna.Domain.Payments;
using Floruna.SharedKernel;

namespace Floruna.Application.Payments;

public sealed record GatewayPixRequest(long AmountCents, Guid OrderId, string CustomerName, string? CustomerEmail, string StoreName, string? NotificationUrl);

public sealed record GatewayPix(string ProviderPaymentId, string PixCode);

/// <summary>O pagamento como o NOSSO domínio o entende (a ACL já traduziu o JSON do provedor).</summary>
public sealed record GatewayPayment(string ProviderPaymentId, ProviderPaymentStatus Status, long AmountCents, Guid? OrderId);

public sealed record GatewayRefund(string ProviderRefundId);

/// <summary>
/// Porta para o gateway (padrão Adapter + Anti-Corruption Layer). A aplicação fala
/// "crie um PIX", "como está este pagamento"; os nomes, códigos e quirks do Mercado
/// Pago ficam do lado de lá. Trocar de gateway = escrever outro adaptador.
/// </summary>
public interface IPaymentGateway
{
    Task<Result<GatewayPix>> CreatePixAsync(string accessToken, GatewayPixRequest request, CancellationToken cancellationToken);

    Task<Result<GatewayPayment>> GetPaymentAsync(string accessToken, string providerPaymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve <paramref name="amountCents"/> do pagamento. Erros: <see cref="GatewayErrors.Rejected"/> é
    /// DEFINITIVO (o provedor recusou: nada saiu); qualquer outro é resultado DESCONHECIDO (timeout, 5xx).
    /// </summary>
    Task<Result<GatewayRefund>> RefundAsync(string accessToken, string providerPaymentId, long amountCents, string idempotencyKey, CancellationToken cancellationToken);
}

public static class GatewayErrors
{
    /// <summary>O provedor respondeu 4xx: a operação NÃO aconteceu e não adianta repetir igual.</summary>
    public const string Rejected = "GATEWAY_REJECTED";
}

/// <summary>
/// Cifra segredos de loja guardados no banco (AES-256-GCM). O <c>context</c> amarra o
/// segredo ao dono: o token da loja A, copiado para a linha da loja B, não decifra.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext, string context);

    /// <summary>Null se o texto foi adulterado, é de outro contexto ou de outra chave.</summary>
    string? Unprotect(string ciphertext, string context);
}

public static class SecretContexts
{
    public static string MercadoPagoToken(TenantId tenantId) => $"mercadopago-token:{tenantId}";
}

public static class PaymentContract
{
    public static string Rail(PaymentRail rail) => rail == PaymentRail.PixOffline ? "pix_offline" : "mercadopago";

    public static string Status(PaymentIntentStatus status) => status.ToString().ToLowerInvariant();
}
