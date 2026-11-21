# Migração do Nest para a API .NET (por loja)

Duas peças:

1. **ETL** (`src/Floruna.Migration`): copia UMA loja do Mongo do Nest para o PostgreSQL, numa transação, e
   CONFERE origem x destino antes de gravar.
2. **Strangler Fig no BFF** (`flora-lis-site/lib/api/routing.ts`, branch `feat/strangler-api-dotnet`): depois do
   ETL, o tráfego daquela loja passa a ir para a API .NET; as outras continuam no Nest.

Decisões e porquês: [ADR 0016](adr/0016-migracao-por-loja.md). Quanto da API do Nest já existe aqui:
[paridade-api.md](paridade-api.md).

## O que o ETL leva

| Do Nest (Mongo) | Para | Observações |
|---|---|---|
| `tenants` | loja | plano, status, teste grátis, domínio próprio, código de indicação (se colidir, gera outro) |
| `adminusers`, `customers` | equipe, clientes | **hash BCrypt do Nest vale**: ninguém troca senha |
| `categories`, `collections` + `collectionproducts`, `products` (+ variações) | catálogo | variação com id antigo fora do padrão ganha id novo determinístico |
| `products.stock` + `stocklots` | estoque | saldo = `product.stock`; lotes com validade e custo (**FEFO preservado**) |
| `coupons`, `deliveryzones` | cupons, zonas | usos do cupom preservados |
| `settings` (pagamento) | configuração de pagamento | PIX; token do Mercado Pago **recifrado** com a chave nova (precisa da chave do Nest) |
| `orders` | pedidos (histórico) | estado, totais, pagamento; **link de acompanhamento antigo continua valendo**; pedidos pagos ganham intenção de pagamento (reembolsáveis) |

IDs são **determinísticos** (UUID v5 do ObjectId): o mesmo documento vira sempre o mesmo id, em todo ensaio.

## Elegibilidade (o ETL recusa sozinho)

- **Pedido pendente** (não pago): estoque "em trânsito" entre os dois sistemas. Espere pagar ou abandonar (3 h).
- **Dado operacional de módulo não importado** (fidelidade, oficinas, orçamentos, caixa, campanhas, avaliações,
  montagem, expedição...): migrar perderia esse dado. `--force` migra mesmo assim e lista o que FICA PARA TRÁS.
- **Registro recusado pelas regras atuais** (ex.: produto sem foto): sem `--force`, nada é gravado; a lista sai no
  relatório para corrigir na origem.
- **Conferência que não bate** (contagens, unidades em estoque, soma dos pedidos em centavos): nada é gravado.
  Não há `--force` para isso.

## Ensaio

```bash
# mesmas variáveis da API (destino e chave de cifra) + a chave do Nest, para trazer o token do gateway
export ConnectionStrings__Floruna="Host=...;Database=floruna;Username=...;Password=..."
export Secrets__EncryptionKey="..."               # a da API .NET
export NEST_SETTINGS_ENCRYPTION_KEY="..."         # SETTINGS_ENCRYPTION_KEY do Nest
dotnet run --project src/Floruna.Migration -- --mongo "$MONGODB_URI" --mongo-db flora-lis --tenant flora-lis --dry-run
```

Na VPS (docs/vps.md), com a mesma imagem da API e as credenciais lidas do servidor (nada na linha de comando):

```bash
cd ~/floruna-dotnet
set -a; . ./.env; set +a
NEST_MONGODB_URI="$(grep -E '^MONGODB_URI=' ~/flora-lis/.env | cut -d= -f2-)" \
NEST_SETTINGS_ENCRYPTION_KEY="$(grep -E '^SETTINGS_ENCRYPTION_KEY=' ~/flora-lis/.env | cut -d= -f2-)" \
docker compose -f docker-compose.prod.yml run --rm --no-deps \
  -e NEST_MONGODB_URI -e NEST_SETTINGS_ENCRYPTION_KEY \
  --entrypoint dotnet api /app/migration/Floruna.Migration.dll --tenant flora-lis --dry-run
```

O `--dry-run` faz TUDO (inclusive a conferência) e desfaz no fim. Repita até o relatório sair limpo.
Para re-ensaiar sobre uma loja já gravada: `--replace` (apaga a loja inteira no PostgreSQL e refaz; as tabelas são
descobertas, não listadas à mão).

## Corte (cutover)

1. **Aviso** à loja: janela curta (madrugada), equipe e clientes vão precisar **entrar de novo**: os tokens do Nest
   carregam o `tenantId` do Mongo, que não existe no .NET.
2. **Congelar escrita no Nest para a loja** (manutenção ou fora do horário) e esperar zerar pedidos pendentes.
3. **ETL de verdade** (sem `--dry-run`). Guarde o relatório.
4. **Virar a chave no BFF**: acrescentar os hosts da loja em `DOTNET_TENANT_HOSTS` (subdomínio e domínio próprio)
   e reiniciar o BFF.
5. **Fumaça**: login da equipe, vitrine, um pedido de teste (PIX), link de acompanhamento antigo, painel de pedidos.
6. **Mercado Pago**: pagamentos criados ANTES do corte continuam notificando a URL do Nest. Mantenha o Nest no ar
   (somente leitura) por ~30 dias para estornos/chargebacks tardios desses pedidos, e registre-os à mão no painel novo.

## Reversão

- **Antes de reabrir a loja** (passo 5 falhou): tire os hosts de `DOTNET_TENANT_HOSTS`. O Mongo está intacto
  (o ETL só LÊ do Mongo).
- **Depois de reabrir**: o que foi gravado no .NET não existe no Mongo. Reverter perde esses dados; não há ETL
  reverso. Por isso o passo 5 é obrigatório e a janela de reversão é curta.

## O que fica de fora (por enquanto)

Fidelidade, oficinas, orçamentos, caixa, substituições, regras de capacidade de entrega (a loja reconfigura),
histórico detalhado de transições de estado do pedido (vira "criado" + "estado migrado"), arquivos de imagem (as URLs
são preservadas; o storage do Nest precisa continuar servindo, ou os arquivos vão para o S3/CloudFront antes).
