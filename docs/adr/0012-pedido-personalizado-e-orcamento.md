# ADR 0012 — Pedido personalizado e orçamento versionado (Fase 6e)

- **Status:** aceita
- **Data:** 2026-11-19

## Decisões

1. **Dois agregados**: `CustomOrderRequest` (o pedido do configurador, com o status simples do painel) e
   `CustomQuote` (cada VERSÃO da proposta). Contrato igual ao do Nest (`/custom-orders`, `/custom-quotes`).
2. **Máquina de estados no domínio (RN-74)**: `draft → published → accepted | rejected | expired`,
   `accepted → converted`. Publicado não muda: revisar = nova versão. A proposta que o cliente viu continua legível.
3. **Token do link**: 24 bytes aleatórios, devolvido UMA vez ao publicar; o banco guarda o SHA-256. Comparação em
   tempo constante. "Não existe" e "token errado" dão a mesma resposta (não revela IDs).
4. **Resposta fora do prazo EXPIRA o orçamento e devolve erro**. Como um `Result` de falha desfaria a transação,
   o handler confirma a expiração e só depois devolve o erro. O job (30 min) expira os esquecidos.
5. **Conversão única**: só `accepted` converte, uma vez; índice único parcial `(loja, pedido convertido)` impede
   ligar o mesmo pedido a dois orçamentos. O pedido precisa existir na loja.
6. **Concorrência**: toda operação de orçamento TRAVA a linha da solicitação (e descarta cópias rastreadas).
   Numeração de versão, "publicar substitui a anterior" e "uma decisão só" acontecem em fila por solicitação.
   Testes: 6 rascunhos simultâneos → versões 1..6; 8 respostas simultâneas → exatamente 1 vale.
   **Teste de mutação**: sem o `FOR UPDATE`, os dois falham em todas as execuções.
7. **O status do painel acompanha a negociação**: publicar → `quoted` (com o valor), aceitar/converter →
   `approved`, recusar → `rejected`. O PATCH manual continua existindo.

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET |
|---|---|---|
| Publicar v2 com v1 publicada | v1 continua aceitável (cliente aceita o preço antigo) | v1 expira ("substituído pela versão 2") |
| Publicar depois de uma versão aceita | permitido | 409 |
| Cliente ver a proposta pelo link | não há rota | `GET /custom-quotes/{id}?token=` |
| Status do pedido x orçamentos | independentes | o status acompanha a negociação |
| Numeração de versão concorrente | `nextVersion` sem trava (índice único estoura) | trava da solicitação |
| Aviso à loja | e-mail best-effort (pode se perder) | evento na outbox (`CustomOrderRequested`); o e-mail é backlog |

## Backlog

- E-mails: aviso à loja (nova solicitação) e ao cliente (proposta enviada, com o link).
- Converter criando o pedido a partir do orçamento (hoje liga a um pedido existente, como no Nest).
- Substituição de item com consentimento do cliente (próxima fatia).
