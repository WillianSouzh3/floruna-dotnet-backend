# ADR 0004 — Estoque (Fase 3)

- **Status:** aceita
- **Data:** 2026-11-18

## Contexto

Flor é perecível: o estoque precisa de lotes com validade, saída FEFO, ledger auditável e nunca vender
além do que existe, inclusive em pico de Dia das Mães. O Nest mantinha `Product.stock` e os lotes como duas
fontes sincronizadas no "melhor esforço" (dívida D-012, documentada pelo próprio projeto).

## Decisões

1. **`StockItem` (um por produto) é a raiz de agregado e CONTÉM os lotes ativos.** O saldo físico é sempre a
   soma dos lotes, recalculada na mesma transação: não existe segunda fonte para divergir. `Product` não tem
   mais estoque.
2. **FEFO como domain service puro** (`FefoAllocator`), tudo-ou-nada. A finalidade muda a elegibilidade:
   **venda** ignora lotes vencidos (RN-26); **descarte/ajuste negativo** consome os vencidos primeiro.
3. **Disponível para venda** = lotes não vencidos − reservas. O campo `stock` do contrato passa a ser esse
   número (o que o front usa para "esgotado"); `availability` traz físico, reservado e vendável (`source: "inventory"`).
4. **Ledger append-only** (`StockMovement`) com snapshot do nome, autor, e de quais lotes saiu cada unidade
   (com custo): base do custo da mercadoria vendida. Chave de idempotência única por loja.
5. **Concorrência pessimista**: `SELECT ... FOR UPDATE` na linha do `stock_items` dentro de transação curta.
   Num produto disputado, a fila no banco é previsível; a otimista viraria tempestade de conflitos. Teste:
   50 reservas simultâneas para 10 unidades → exatamente 10 sucessos.
6. **Reserva** (`held → committed | released | expired`) com idempotência por chave e transições
   idempotentes. Liberar/expirar trava a reserva e o estoque (ordem fixa: reserva → estoque).
7. **Job de expiração orientado a trabalho** (consulta global por índice parcial, contexto aberto por item),
   seguro com várias réplicas sem lock distribuído (trava de linha + transição idempotente).
8. **Sem retry automático do EF** (`EnableRetryOnFailure` removido): repetir uma transação explícita
   re-aplicaria regras em agregados já alterados em memória. Falha transitória vira erro; o cliente repete
   com a mesma chave de idempotência. `ExecuteInTransactionAsync` limpa o `ChangeTracker` em falha.
9. **Estoque no formulário do produto vira AJUSTE no ledger** (o Nest sobrescrevia o número sem rastro).
10. **Migration com backfill testada**: `products.stock` vira `stock_items` + lote + movimento
    `opening_balance`; a coluna só é removida depois (a ordem gerada pelo EF apagaria os saldos).

## Consequências

- CHECK constraints repetem as invariantes no banco (`on_hand ≥ 0`, `0 ≤ reserved ≤ on_hand`, `0 ≤ remaining ≤ inicial`).
- O `Down` da migration não restaura os saldos antigos (aceito: rollback de dados é por backup).
- Escape do filtro de loja só em arquivos de uma allowlist verificada por teste de arquitetura.
- **Backlog**: itens de inventário/materiais e composições (receita do buquê, D-004/D-014), locais de estoque
  (filiais), reconciliação ledger × projeção, vida útil mínima até a data prometida de entrega (Fase 4, junto da
  promessa de entrega).
