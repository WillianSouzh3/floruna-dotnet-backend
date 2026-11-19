# ADR 0013 — Substituição com consentimento (Fase 6f)

- **Status:** aceita
- **Data:** 2026-11-19

## Contexto

No Nest havia DOIS fluxos desconectados: a proposta com consentimento (política, link, prazo), ligada às
ordens de montagem (não portadas), e a rota do operador `POST /orders/:id/resolutions/substitution`, que trocava o
item NA HORA, sem consentimento nenhum, contrariando a própria D-015 ("nada é aplicado sem autorização válida").

## Decisões

1. **Um fluxo só, sobre o pedido**, mantendo a rota do Nest. O modo escolhido no checkout é CONGELADO na proposta:
   - `exact`: recusa na origem (409);
   - `preapproved` dentro da tolerância da política: nasce aprovada (`decidedBy: system`) e é aplicada na mesma
     transação (200, mesmo comportamento da rota do Nest);
   - o resto: pergunta ao cliente, com link (token mostrado uma vez, hash no banco) e prazo (202).
2. **Tolerância nos DOIS sentidos**: o "equivalente" é a variação do valor de catálogo dentro de ±X% (padrão 10%).
   Um substituto bem mais barato rebaixa o arranjo; um bem mais caro muda a natureza dele. O Nest só olhava o
   encarecimento.
3. **Aplicar é a porta única** (`SubstitutionApplier`): só a partir de `approved`, com o pedido em
   `confirmado`/`em_preparo`; baixa o substituto (e devolve o original se pedido) com estoque travado em ordem
   crescente de produto. Os ITENS do pedido não mudam (registro da venda); a troca fica em `substitutions`.
4. **Aprovação do cliente aplica automaticamente, em outra transação**: se faltar estoque nesse instante, a
   aprovação NÃO se perde; a proposta fica `approved` e a loja aplica depois (`POST /substitutions/{id}/apply`).
5. **"Perguntar antes" só o cliente aprova**; a loja pode registrar recusa (ex.: resposta por telefone).
6. **Resposta fora do prazo expira e devolve erro** (gravado após o commit, como nos orçamentos); job de 10 min
   expira as esquecidas. **Concorrência**: proposta travada em toda decisão/aplicação; respostas simultâneas →
   exatamente uma vale. **Teste de mutação**: sem o `FOR UPDATE`, falha.
7. **Política versionada** (`/substitution-policy`, nomes do Nest): cada publicação incrementa a versão; a proposta
   guarda a versão sob a qual foi decidida.

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET |
|---|---|---|
| Troca pelo operador | aplicada na hora, sem consentimento | consentimento conforme o modo do pedido |
| Tolerância | só encarecimento | ± variação de valor |
| Base do valor | custo (inventário/montagem) | preço de catálogo (o que o cliente percebe) |
| Cliente ver a proposta | sem rota | `GET /substitutions/{id}?token=` |
| Aprovação sem estoque | n/a | aprovação fica; aplicação manual depois |

## Backlog

- E-mail/WhatsApp ao cliente com o link da proposta (evento `SubstitutionChanged` já vai para a outbox).
- Substituição ligada à fila de montagem (D-004/D-014), quando ela for portada.
