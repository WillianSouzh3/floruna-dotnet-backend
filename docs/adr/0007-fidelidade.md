# ADR 0007 — Fidelidade: cashback e níveis (Fase 6a)

- **Status:** aceita
- **Data:** 2026-11-19

## Contexto

O cashback é **passivo financeiro** da loja: cada real distribuído é dívida em mercadoria. Por isso o modelo precisa
ser auditável (de onde veio cada centavo), à prova de retry (crédito duplicado é dinheiro dado de graça) e à prova de
concorrência (o mesmo saldo gasto em dois pedidos).

## Decisões

1. **Política de desconto como função pura** (`DiscountPolicy.Resolve`), escrita em TDD com os 13 casos do
   `loyalty-policy.spec.ts`: `best_of` (empate → cupom), teto de desconto total (corta o nível primeiro), frete grátis,
   cashback com piso de resgate e teto por pedido (`floor`). Níveis também puros (`LoyaltyTiers.Progress`): requisitos
   **conjuntivos**, último nível cumprido na ordem da lista, progresso pelo requisito **mais atrasado**.
2. **Razão append-only** (`loyalty_entries`). Créditos são **lotes** com `remaining` e validade; débitos não têm saldo.
   Estorno é linha nova de sinal contrário, nunca edição. O saldo na conta é cache, recalculável pelos lotes.
3. **Resgate consome o lote que vence primeiro**: o cliente nunca perde saldo que poderia ter usado.
4. **Idempotência em duas camadas**: `dedupeKey` derivada do fato (`cashback:earn:{pedido}`) com índice único por loja,
   e marcadores no pedido (`loyalty_accrued_at`, `loyalty_reversed_at`) gravados na MESMA transação do crédito.
5. **Carteira = e-mail normalizado na loja** (comprar sem login também acumula). **Resgate exige sessão**, e a carteira
   debitada é a do **e-mail do token**, nunca a digitada no checkout. Digitar o e-mail de outra pessoa não gasta o
   saldo dela (teste).
6. **Resgate dentro da transação do checkout**, com trava da conta (`FOR UPDATE`). Se o pedido não for gravado, o
   saldo não sai. Seis checkouts simultâneos com o saldo inteiro: só um usa (teste).
7. **Acúmulo na entrega e devolução no cancelamento via outbox** (`OrderStatusChanged`). Os dois lados do mesmo fato
   ficam no mesmo handler. A devolução acontece mesmo se o programa for desligado depois: o saldo era do cliente.
8. **Expiração**: o resgate já ignora lote vencido; um job (mesmo desenho do job de reservas: orientado a trabalho,
   uma conta por escopo, trava) zera os lotes e registra `expired` no razão, para saldo e passivo ficarem exatos.
9. **RN-34**: o cashback pode quitar no máximo 90% do pedido (configuração validada no domínio e com `CHECK` no banco).
   PIX de R$ 0,00 é cobrança inválida.

## Lição: identity resolution do EF pode anular uma trava pessimista

O teste de concorrência falhou na primeira versão: **quatro** checkouts gastaram o mesmo saldo. A cotação (antes da
transação) lia a conta **rastreada**. Depois do `SELECT ... FOR UPDATE`, a consulta devolvia a MESMA instância em
memória, porque o EF não sobrescreve entidade já rastreada. A trava existia, mas o código lia o saldo antigo.
Correção: leituras fora da trava são `AsNoTracking`. Regra geral: **num DbContext que vai travar uma linha, não
carregue a mesma entidade rastreada antes da trava.** Os outros fluxos com trava (estoque, reservas) foram revisados
e já travam antes de ler.

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET |
|---|---|---|
| Pontos (avaliações, indicação) | sim | **fora**: só cashback (pontos = 2ª moeda, mesmo razão; backlog) |
| Opt-in (`requireOptIn`, `/me/opt-in`) | sim | carteira nasce automaticamente; backlog |
| Cashback por categoria, automações de cupom | sim | backlog |
| Idempotência do acúmulo | escrita condicional em `loyaltyAccruedAt` | marcador no pedido + índice único da `dedupeKey` |
| Saldo do cliente em `/me` | saldo gravado | saldo **utilizável agora** (lote vencido não conta, mesmo antes do job) |

## Backlog

- Pontos, opt-in/opt-out (LGPD), cashback por categoria, indicação.
- E-mail de "subiu de nível" e de "seu saldo vence em N dias" (outbox).
- Rebaixamento de nível em estorno pós-entrega (depende dos reembolsos da Fase 6b).
