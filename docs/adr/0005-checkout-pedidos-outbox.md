# ADR 0005 — Checkout, pedidos e outbox (Fase 4)

- **Status:** aceita
- **Data:** 2026-11-18

## Decisões

1. **Checkout numa transação** (RN-42): trava o estoque de cada produto, cria reservas, consome o cupom
   (UPDATE condicional), ocupa a vaga da janela (UPSERT condicional), grava o pedido e o evento
   `OrderPlaced` na outbox. Nenhuma chamada externa lá dentro.
2. **Travas sempre na mesma ordem** (produto crescente) no checkout, na confirmação e no cancelamento:
   dois pedidos com os mesmos itens em ordem diferente nunca entram em deadlock.
3. **Preço só do servidor** (RN-40) e **snapshots em jsonb** (itens, cliente, endereço, cartão, histórico):
   documentos imutáveis do pedido.
4. **Idempotência** pela chave (RN-41): leitura antes; na corrida, o índice único decide e a requisição que
   perde devolve o pedido vencedor. Teste: 8 requisições simultâneas com a mesma chave → 1 pedido.
5. **Máquina de estados** com `expectedState` + `idempotencyKey` (D-007): replay é no-op; estado divergente é 409.
6. **Reserva no checkout, venda na confirmação**: confirmar converte cada reserva em `venda` no ledger
   (chave `sale:{pedido}:{item}`, idempotente). Cancelar pendente devolve reserva, vaga e uso do cupom.
   Cancelar após confirmado NÃO devolve estoque (perecível exige inspeção; regra floral 7).
7. **Pedido abandonado** (3 h sem confirmação) é cancelado pelo job com ator `system` e chave fixa.
8. **Entrega**: zonas por faixa de CEP (menor taxa vence), regras `recurring` / `date` / `blackout` com precedência
   independente da ordem de cadastro, corte em horário LOCAL da loja, ocupação contada no escopo da regra.
   **Desvio consciente do Nest**: a cotação (`/delivery-promises/quote`) não é um token de uso único;
   o checkout REFAZ a conta na hora de gravar (mais forte que confiar numa cotação antiga).
9. **Transactional Outbox**: o interceptor grava os eventos no mesmo `SaveChanges`; o processador reivindica
   lotes com lease (`FOR UPDATE SKIP LOCKED`), entrega a cada handler com **recibo por handler**, faz backoff
   exponencial e manda para a **fila de mortas** após 10 tentativas (corrige A2), com operação de reprocessar
   para o superadmin. Handlers são registrados pelos módulos (corrige M3); a busca é por trabalho, não por loja (M4).
10. **Papéis do processo**: `BackgroundJobs:Enabled` separa `api` de `worker` com a mesma imagem (corrige M5).
11. E-mail de pedido sai pela outbox (`IEmailDelivery` síncrono, falha = retry). E-mails de autenticação seguem na fila
    em memória (precisam sair em segundos e são re-solicitáveis).

## Backlog

- Pagamentos (Fase 5): PIX, Mercado Pago, `paymentStatus` real (hoje fixo em `pending`).
- Pontos de retirada (hoje `retirada` é na loja), campos extras configuráveis, taxa de cartão-presente
  (dependem de configurações da loja), cupom restrito por categoria, desempenho do cupom.
- PDV, reembolsos e resoluções, aprovação de foto do buquê, substituição com consentimento (Fase 6).
- Vida útil mínima do lote até a data prometida (RN-26 completo).
