# ADR 0010 — PDV e caixa (Fase 6d)

- **Status:** aceita
- **Data:** 2026-11-19

## Decisões

1. **Venda de balcão é um `Order` com origem `pdv`** (`Order.PlacePos`): nasce PAGA e ENTREGUE, com pagamento dividido
   (`dinheiro`, `pix`, `cartao_debito`, `cartao_credito`, `outro`; até 5 parcelas). A soma das parcelas precisa bater
   com o total, centavo a centavo (domínio). Mesmo núcleo do checkout online: preço do catálogo (`OrderItemPricer`,
   extraído do checkout), trava do estoque em ordem crescente de produto, ledger com a chave `sale:{pedido}:{item}`,
   tudo numa transação. Sem entrega, cupom, gateway nem e-mail.
2. **Idempotência opcional** (`idempotencyKey`): o duplo clique em "finalizar venda" devolve a mesma venda e baixa o
   estoque uma vez (teste com 4 requisições simultâneas).
3. **Caixa = `CashSession`**. O esperado nunca é digitado: fundo + vendas em dinheiro + suprimentos − sangrias. O
   pedido guarda `cash_received_cents` (parte em dinheiro), e a conferência soma por índice. Fechar congela esperado,
   contado e diferença (quebra de caixa).
4. **Um caixa aberto por operador**: índice único parcial (`WHERE status = 'Open'`). Cinco "abrir caixa" simultâneos:
   um abre, quatro recebem 409.
5. **A corrida entre venda e fechamento** (no Nest, uma venda podia ser carimbada num caixa que estava fechando e ficar
   fora da conferência):
   - a venda trava o caixa com `FOR SHARE`: várias vendas andam em paralelo;
   - movimento e fechamento travam com `FOR UPDATE`: esperam as vendas em andamento e SÓ ENTÃO somam;
   - uma venda que chega durante o fechamento espera; quando ele confirma, o PostgreSQL reavalia o `WHERE` na versão
     nova da linha (`status` já não é `Open`) e a venda segue sem caixa, como no Nest quando não há caixa aberto.

   Teste: 12 vendas em dinheiro disputando com o fechamento; o esperado no fechamento é exatamente o fundo + o dinheiro
   das vendas carimbadas. **Teste de mutação**: sem o `FOR SHARE`, o teste falhou 3 em 3 execuções.
6. **Sangria não deixa a gaveta negativa** (domínio). Plano: `Pdv` (Pro+). Papéis: dono, gerente e `caixa`.

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET |
|---|---|---|
| Venda durante o fechamento do caixa | pode ficar fora da conferência | trava compartilhada x exclusiva |
| Sangria maior que a gaveta | aceita | 400 |
| Duplo clique na venda | duas vendas | `idempotencyKey` opcional |
| Subtotal do PDV | preço de tabela, promoção vira desconto | preço efetivo (igual ao checkout online desta API) |
| Emissão fiscal (NFC-e), contas a receber | sim | fora do escopo (backlog) |

## Backlog

- NFC-e/NF-e e financeiro (contas a receber), cortesia/desconto acima de X% só com gerente.
- Troco (valor recebido x valor da parcela) e reembolso de venda de balcão.
