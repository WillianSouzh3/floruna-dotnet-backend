# ADR 0003 — Catálogo (Fase 2)

- **Status:** aceita
- **Data:** 2026-11-18

## Contexto

Produtos com variações, categorias dinâmicas (taxonomia) e coleções com janela de tempo (curadoria),
com o mesmo contrato do Nest (`/products`, `/categories`, `/collections`). A vitrine é a rota mais
acessada do sistema, principalmente em datas comemorativas.

## Decisões

1. **Três agregados**: `Product` (com `ProductVariant` e vínculos de categoria como *owned entities*),
   `Category` e `Collection` (com itens ordenados). Referência entre agregados só por ID.
2. **Variação com identidade estável** (`var_` + 24 hex, formato do Nest). A reconciliação na atualização
   preserva o ID pelo ID ou pelo nome (D-003), para pedidos e estoque não quebrarem com renomeações.
3. **Dinheiro em colunas de centavos** (`price_cents`) com a visão `Money` calculada no domínio. Motivo:
   filtros como "preço efetivo entre X e Y" (`COALESCE(discount, price)`) precisam ser traduzíveis para SQL.
   CHECK constraints no banco repetem as invariantes (preço > 0, promocional < preço).
4. **CQRS de verdade**: comandos carregam o agregado e passam pelo domínio; consultas usam uma porta própria
   (`ICatalogQueries`) com `AsNoTracking` e devolvem DTOs prontos.
5. **Cache-aside com `HybridCache`** na vitrine (lista, produto por slug, categorias), chave e TAG por loja;
   toda escrita no catálogo da loja invalida a tag. Proteção contra *stampede* embutida. O checkout (Fase 4)
   nunca usa o cache para preço.
6. **PATCH real**: `Optional<T>` + conversor JSON distinguem campo ausente de `null` (ex.: `discountPrice: null`
   remove a promoção; ausente mantém).
7. **Limites do plano** (produtos e fotos por produto) aplicados no caso de uso, pelo plano efetivo.

## Consequências e dívidas conscientes

- `Product.Stock` é **transitório**: na Fase 3 a disponibilidade passa a vir do estoque (lotes + ledger) e o
  campo vira saldo inicial migrado (*Branch by Abstraction*, como a nota 028 do Nest). Hoje `availability.source = "product"`.
- Unicidade de SKU de **variação** é verificada na aplicação (a tabela owned não tem `tenant_id`); SKU e código de
  barras do **produto** têm índice único parcial por loja.
- Busca por `ILIKE` (sem acento-insensível nem relevância). Evolução: `unaccent` + `pg_trgm` ou `tsvector`.
- Com várias réplicas e sem Redis (L2), a invalidação por tag é local: até 5 min de vitrine desatualizada em outra
  réplica. Aceito para vitrine; Redis entra na Fase 7.
- Eventos de domínio do catálogo (`catalog.variant.*`) serão publicados pela outbox na Fase 4.
- Campos fiscais (NCM, CFOP...) saem vazios até o módulo fiscal (backlog).
