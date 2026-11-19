# ADR 0006 — Pagamentos (Fase 5)

- **Status:** aceita
- **Data:** 2026-11-18

## Decisões

1. **BR Code PIX como função pura de domínio** (`PixBrCode`), escrita em TDD com os casos do teste Jest do Nest e o
   vetor oficial do CRC16-CCITT-FALSE. Validado também por implementação independente (Python) sobre um código real.
2. **`PaymentIntent` por pedido** (D-009) com livro-razão append-only de transações (webhook, manual, reconciliação).
   O estado do pagamento é separado do estado do pedido; o pedido guarda só uma cópia (`paymentStatus`, `pixCode`).
3. **Mercado Pago atrás de `IPaymentGateway`** (Adapter + Anti-Corruption Layer): status, centavos e nomes do provedor
   são traduzidos no adaptador. `HttpClient` com **resiliência padrão** (timeout por tentativa, retry com jitter,
   circuit breaker). Retry do POST é seguro por causa do `X-Idempotency-Key`.
4. **Cobrança depois do commit do checkout** (RN-42). Gateway fora + contingência habilitada → PIX direto
   (`fallbackUsed`). Sem configuração → pedido criado com `paymentInitializationStatus: failed`.
5. **Webhook = inbox + outbox**: responde 200 sempre, grava o aviso e um evento; o processamento roda na outbox
   (retry, backoff, fila de mortas). **Verify-by-fetch**: só o ID é usado; o status vem de uma consulta ao provedor
   com o token da loja. Webhook forjado não aprova pedido (teste).
6. **Confirmação em dois passos idempotentes**: (1) fato + confirmação do pedido no mesmo commit (comando de status com
   chave `payment:{intenção}`); (2) se o pedido não puder ser confirmado (cancelado por abandono, sem estoque), o
   pagamento é gravado sozinho com `reconciliationIssue`. **Dinheiro recebido nunca é perdido num rollback.**
7. **Confirmação manual** (D-010): exige valor exato e evidência; auditada pelo livro-razão; idempotente pela chave.
8. **Token do gateway cifrado** com AES-256-GCM, contexto = loja (dados associados): copiar o valor cifrado para outra
   loja não decifra. Token só de escrita na API. Chave em `Secrets:EncryptionKey` (32 bytes), validada na subida.

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET |
|---|---|---|
| Configuração de pagamento | dentro de `/settings` (DTO gigante) | recurso `/settings/payments` |
| Webhook | inbox própria + crons de replay | aviso grava evento na outbox (reaproveita retry/DLQ) |
| `external_reference` | token de acesso do pedido | ID do pedido |
| Pagamento após cancelamento | depende do fluxo | sempre gravado e marcado para reconciliação |

## Backlog

- Reembolso (integral no cancelamento pago, parcial pós-venda) e cartão via Checkout Pro (Fase 6).
- Reconciliação periódica de intenções pendentes (polling do gateway) e recuperação de inicializações falhas.
- Assinatura `x-signature` do webhook (camada extra além do verify-by-fetch).
- Na AWS: chave de cifra no Secrets Manager hoje; evolução para *envelope encryption* com KMS.
