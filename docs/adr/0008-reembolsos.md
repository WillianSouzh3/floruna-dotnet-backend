# ADR 0008 — Reembolsos (Fase 6b)

- **Status:** aceita
- **Data:** 2026-11-19

## Contexto

Reembolso é a operação em que um erro custa dinheiro de verdade: devolver duas vezes, devolver mais do que foi pago,
ou cancelar o pedido sem devolver. E o passo principal acontece FORA do nosso banco (no gateway), então não cabe numa
transação.

## Decisões

1. **Saga em três passos** (`RefundSaga`):
   1. **Reserva**: transação curta com a intenção de pagamento travada (`FOR UPDATE`). O valor sai do "reembolsável"
      (`refund_reserved_cents`) e nasce um `Refund` pendente.
   2. **Gateway**: chamado SEM transação aberta, com `X-Idempotency-Key = refund-{id do reembolso}`.
   3. **Conclusão**: outra transação. Sucesso consolida (reservado → reembolsado); recusa devolve a reserva.
2. **Recusa ≠ resultado desconhecido.** 4xx do gateway = recusa definitiva: libera a reserva. Timeout/5xx = o dinheiro
   PODE ter saído: o reembolso continua **pendente** (reserva presa), com backoff exponencial, e um job repete com a
   **mesma chave**. O gateway deduplica: devolve o mesmo reembolso em vez de pagar de novo (teste com resposta perdida).
   Depois de 10 tentativas sem resposta, para e marca `refund_unconfirmed` para conferência humana.
3. **Invariante no domínio e no banco**: `reembolsado + reservado <= pago` (`ReserveRefund` e `CHECK` na tabela).
   Cinco reembolsos parciais simultâneos de R$ 30 num pedido de R$ 100: exatamente três passam (teste).
4. **Cancelar pedido pago = reembolsar ANTES de cancelar.** Pré-checagens (estado esperado, transição permitida) vêm
   antes de devolver dinheiro. Recusa → 502 `REFUND_NOT_CONFIRMED` e o pedido continua como estava. Pendente → 409
   "em processamento"; quando o job confirmar, o evento `RefundSucceeded` (outbox) cancela o pedido. Se o
   cancelamento falhar depois do dinheiro devolvido: `refund_completed_order_not_cancelled` (nunca some em silêncio).
5. **Lease para quem chama o gateway**: a requisição que cria o reembolso tem a vez (lease de 60 s na criação); o job
   reivindica com `UPDATE ... WHERE lease vencido` (competing consumers sem lock distribuído).
6. **Reembolso parcial pós-venda** (`POST /orders/{id}/resolutions/refund`): valor, motivo, itens devolvidos
   (validados contra o vendido menos o já devolvido) e `restock`. Itens voltam ao estoque com o custo congelado na
   venda (movimento `Devolucao`), só depois do dinheiro devolvido. 200 = confirmado, 202 = pendente.
7. **PIX direto**: método `manual`. A loja devolve pelo banco; o sistema registra (mesma trilha, mesma auditoria).
8. **Os locks devolvem dado fresco**: o repositório descarta a cópia rastreada antes de ler a linha travada
   (generalização da lição da ADR 0007). O pedido recebe o total reembolsado ABSOLUTO lido da intenção travada, nunca
   "soma em cima" do valor em memória.

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET |
|---|---|---|
| Timeout no reembolso parcial | libera a reserva e responde 502 (pode reembolsar duas vezes) | fica pendente, 202, job repete com a mesma chave |
| Chave do gateway no parcial | `refund-partial-{pedido}-{valor acumulado}` | id do reembolso |
| Cancelar pedido pago com PIX direto | bloqueado (sem id do gateway) | reembolso manual registrado |
| Histórico de resoluções | `order.resolutions` | tabela `refunds` (em `GET /payments/intents?orderId=`); pedido traz `refundedAmount`/`refundStatus` |
| Substituição de item | sim | backlog |
| Erro de gateway | 502 com código genérico | 502 `REFUND_NOT_CONFIRMED` |

## Backlog

- Substituição de item (`/resolutions/substitution`).
- Estorno proporcional do cashback em reembolso parcial pós-entrega.
- Reembolso iniciado no painel do Mercado Pago (webhook `refunded` sem `Refund` local): hoje só muda o status.
