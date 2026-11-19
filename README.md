# Floruna API (.NET)

Reescrita do backend do [Floruna](../flora-lis-site) (plataforma SaaS para floriculturas) em **C# / ASP.NET Core 10**,
com Clean Architecture, DDD, CQRS, TDD e SOLID. Projeto de estudo e de portfólio, com o mesmo
contrato HTTP da API NestJS para continuar servindo o front Next.js.

- Plano completo, requisitos e regras de negócio: [`docs/00-PLANO-REESCRITA-DOTNET.md`](docs/00-PLANO-REESCRITA-DOTNET.md)
- Decisões: [`docs/adr/`](docs/adr)

## Estrutura

```
src/
  Floruna.SharedKernel    Result, Error, Entity, AggregateRoot, Money       (sem dependências)
  Floruna.Domain          regras de negócio puras                            → SharedKernel
  Floruna.Application     CQRS: mediator próprio, behaviors, casos de uso    → Domain
  Floruna.Infrastructure  EF Core/PostgreSQL, gateways, e-mail               → Application
  Floruna.Api             Controllers, envelope do contrato, DI (composition root)
tests/
  Floruna.Domain.UnitTests        Money, Result, agregados
  Floruna.Application.UnitTests   mediator, pipeline, validação
  Floruna.ArchitectureTests       regra de dependência entre camadas
  Floruna.Api.IntegrationTests    contrato HTTP, auth, isolamento entre lojas (PostgreSQL real)
```

## Rodando

Requer o **.NET 10 SDK** (`sudo apt install dotnet-sdk-10.0`) e Docker (os testes de integração sobem um
PostgreSQL real com Testcontainers).

```bash
dotnet test                          # todos os testes
dotnet run --project src/Floruna.Api # http://localhost:4000/api/v1/health
docker compose up --build            # API + PostgreSQL em containers
```

- Health: `GET /api/v1/health/live`, `/api/v1/health/ready`
- Documentação interativa (Scalar): `/api/docs`
- OpenAPI: `/api/openapi/v1.json`

Sem o SDK instalado, dá para usar o SDK em container:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

## Endpoints (mesmo contrato da API NestJS)

| Método | Rota | Acesso |
|---|---|---|
| POST | `/api/v1/tenants/signup` | público, rate-limited |
| POST | `/api/v1/auth/admin/login` | público, rate-limited |
| POST | `/api/v1/auth/register` · `/api/v1/auth/login` | público (cliente), rate-limited |
| GET | `/api/v1/auth/me` | autenticado |
| POST | `/api/v1/auth/admin/verify-otp` | público (2FA, quando há SMTP) |
| POST | `/api/v1/auth/admin/forgot-password` · `/reset-password` | público, rate-limited |
| POST | `/api/v1/auth/admin/revoke-sessions` | `superadmin`, `master`, `gerente` |
| POST | `/api/v1/auth/admin/impersonate/{tenantId}` | `superadmin` (auditado) |
| POST | `/api/v1/auth/admin/impersonation/exchange` | público (código de 60 s) |
| GET · POST · PATCH · DELETE | `/api/v1/users`, `/users/{id}`, `/users/me/password` | equipe (`master` gerencia) |
| GET | `/api/v1/tenants/me` | equipe da loja |
| GET · POST · DELETE | `/api/v1/tenants/me/domain`, `POST /tenants/me/domain/verify` | `master` (plano com domínio próprio) |
| GET | `/api/v1/products` (filtros: `category`, `collection`, `search`, `onlyActive`, `onlyFeatured`, `minPrice`, `maxPrice`, `page`, `limit`) · `/products/{slug}` | público (com cache) |
| GET | `/api/v1/products/manage` | equipe + caixa (inclui custo) |
| POST · PUT · DELETE | `/api/v1/products`, `/products/{id}` | admin |
| GET | `/api/v1/categories` · `/collections` · `/collections/slug/{slug}` | público |
| GET · POST · PATCH · DELETE | `/api/v1/categories/manage`, `/categories/{id}`, `/collections/manage`, `/collections/{id}`, `PUT /collections/{id}/products` | admin |
| GET | `/api/v1/stock/low` · `/low/count` · `/expiring?days=7` · `/reservations` · `/{productId}/movements` · `/{productId}/lots` | admin |
| POST | `/api/v1/stock/{productId}/restock` · `/adjust` · `/loss` · `/reservations/{id}/release` | admin |
| POST | `/api/v1/orders` (checkout; login opcional; `useCashback` exige sessão) · GET `/orders/{id}?token=` | público (rate-limited) |
| GET | `/api/v1/orders` (filtros: `status`, `search`, `minTotal`, `maxTotal`) · PATCH `/orders/{id}/status` | admin |
| GET | `/api/v1/orders/me` | cliente |
| POST | `/api/v1/orders/{id}/resolutions/refund` (reembolso parcial; 202 se pendente) | admin |
| POST | `/api/v1/orders/pos` (venda de balcão) · GET `/cash-sessions/current` · POST `/cash-sessions/open` · `/movement` · `/close` · GET `/cash-sessions` | PDV (plano Pro+) |
| GET · POST · PUT · DELETE | `/api/v1/coupons` · GET `/coupons/validate?code=&subtotal=&email=` (público) | admin |
| GET · POST · PUT · DELETE | `/api/v1/delivery-zones` · GET `/delivery-zones/quote?cep=` (público) | admin |
| GET · POST · PATCH · DELETE | `/api/v1/delivery-capacity` · POST `/delivery-promises/quote` (público) | admin |
| GET · POST | `/api/v1/platform/outbox?state=dead` · `/platform/outbox/{id}/retry` | superadmin |
| GET · PUT | `/api/v1/settings/payments` (token do gateway só escrita) | admin |
| POST | `/api/v1/payments/webhook?tenant={slug}` | público (sempre 200; verify-by-fetch) |
| GET · POST | `/api/v1/payments/intents?orderId=` · `/intents/{id}` · `/intents/{id}/confirm-manual` | admin |
| GET · PUT | `/api/v1/loyalty/program` · GET `/loyalty/accounts` · `/loyalty/liability` · POST `/loyalty/accounts/{id}/adjust` | admin (plano Pro+) |
| GET | `/api/v1/loyalty/me` · `/loyalty/me/statement` | cliente |
| GET | `/api/v1/events` · `/events/{slug}` · POST `/events/{id}/registrations` | público |
| POST · PUT · DELETE | `/api/v1/events` · GET `/events/{id}/registrations` | admin |
| POST | `/api/v1/custom-orders` · POST `/custom-quotes/{id}/decision` · GET `/custom-quotes/{id}?token=` | público |
| GET · PATCH | `/api/v1/custom-orders` · `/custom-orders/{id}/quotes` · POST `/custom-quotes/{id}/publish` · `/convert` | admin |
| POST · GET | `/api/v1/orders/{id}/resolutions/substitution` (200 aplicada / 202 aguardando) · `/orders/{id}/substitutions` · POST `/substitutions/{id}/decision` · `/apply` · GET · PUT `/substitution-policy` | admin |
| POST · GET | `/api/v1/substitutions/{id}/consent` · `/substitutions/{id}?token=` | público (link) |
| GET | `/api/v1/health`, `/health/live`, `/health/ready` | público |

A loja vem do header `x-tenant-host` (enviado pelo BFF) ou do `Host`: `flora-lis.localhost:3000` → loja `flora-lis`.

```bash
docker compose up --build -d
curl -X POST localhost:4000/api/v1/auth/admin/login -H 'x-tenant-host: flora-lis.localhost' \
  -H 'content-type: application/json' -d '{"email":"admin@floraelis.com.br","password":"troque-esta-senha-local"}'
```

## Configuração (Options Pattern, validada na subida)

| Chave | Variável de ambiente | Exemplo |
|---|---|---|
| `ConnectionStrings:Floruna` | `ConnectionStrings__Floruna` | `Host=...;Database=floruna;...` |
| `Jwt:Secret` (≥ 32) | `Jwt__Secret` ou `AUTH_SECRET` | **o mesmo do front** |
| `Jwt:StaffTokenLifetime` / `CustomerTokenLifetime` | `Jwt__...` | `08:00:00` / `7.00:00:00` |
| `Platform:Domain` / `DefaultTenantSlug` | `Platform__Domain` | `floruna.com.br` / `flora-lis` |
| `Database:MigrateOnStartup` | `Database__MigrateOnStartup` | `true` só em dev |
| `Seed:AdminEmail` / `AdminPassword` | `Seed__AdminEmail` | cria loja + master (dev) |
| `RateLimiting:AuthPermitLimit` | `RateLimiting__AuthPermitLimit` | `5` por minuto |
| `Smtp:Host` / `Port` / `Username` / `Password` / `From` | `Smtp__Host` | sem host = e-mail e 2FA desligados |
| `Authentication:TwoFactorEnabled` | `Authentication__TwoFactorEnabled` | `true` (só vale com SMTP) |
| `Network:TrustedProxies` | `Network__TrustedProxies__0` | `10.0.0.0/16` (VPC do ALB) |
| `Seed:SuperAdminEmail` / `SuperAdminPassword` | `Seed__SuperAdminEmail` | superadmin de dev |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0` | `http://localhost:3000` |
| `Secrets:EncryptionKey` | `Secrets__EncryptionKey` | 32 bytes base64 (`openssl rand -base64 32`), **obrigatória** |
| `Platform:ApiPublicUrl` | `Platform__ApiPublicUrl` | URL pública da API (webhook do gateway) |
| `MercadoPago:BaseUrl` | `MercadoPago__BaseUrl` | `https://api.mercadopago.com/` |
| `BackgroundJobs:Enabled` | `BackgroundJobs__Enabled` | `false` = processo só HTTP (papel api) |
| `GIT_SHA` | `GIT_SHA` | SHA do commit (preenchido pelo CI na imagem) |

Configuração inválida impede a API de subir (fail-fast).

## Observabilidade

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889 docker compose --profile observability up --build
# painel: http://localhost:18888 (traces, métricas, logs)
```

Toda resposta traz `X-Trace-Id`: com ele se acha a requisição inteira no painel, inclusive o que a outbox fez depois.
Métricas de negócio no medidor `Floruna` (`floruna.orders.*`, `floruna.outbox.*`...). Detalhes: ADR 0014.

## Infraestrutura (AWS)

CDK em C# em `infra/` (precisa de Node.js 22 e .NET 8+). Guia completo: [docs/aws.md](docs/aws.md).

```bash
cd infra
dotnet test                                    # asserções no CloudFormation + cdk-nag
npx aws-cdk@2 synth -c env=staging -c imageTag=<sha>
npx aws-cdk@2 deploy --all -c env=prod -c imageTag=<sha> -c certificateArn=arn:aws:acm:...
```

A imagem também roda como migrator: `docker run <imagem> migrate` aplica as migrations e sai.

## Roadmap

| Fase | Conteúdo | Status |
|---|---|---|
| 0 | Fundação: solution, mediator, envelope, Options, health, CI, testes de arquitetura | ✅ |
| 1 | Plataforma + Identity: tenant por host, filtro fail-closed, JWT compatível, planos, 2FA, bloqueio, reset de senha, equipe, superadmin, domínio próprio | ✅ |
| 2 | Catálogo: produtos com variações, categorias, coleções com janela, cache da vitrine, limites do plano | ✅ |
| 3 | Estoque: lotes com validade, FEFO, ledger append-only, reservas com expiração, concorrência com trava de linha | ✅ |
| 4 | Checkout + Pedido + Outbox: cupons, zonas e capacidade de entrega, checkout transacional e idempotente, estados, abandonados, outbox com DLQ | ✅ |
| 5 | Pagamentos: BR Code PIX, Mercado Pago com resiliência, webhook verificado, confirmação manual, token cifrado | ✅ |
| 6a | Fidelidade: cashback com razão de lotes, níveis, resgate no checkout com trava, acúmulo/estorno pela outbox, expiração | ✅ |
| 6b | Reembolsos: saga com o gateway, recusa x resultado desconhecido, retentativa idempotente, cancelamento de pago, devolução ao estoque | ✅ |
| 6c | Oficinas: vagas atômicas (UPDATE condicional + inscrição na mesma transação), CHECK no banco | ✅ |
| 6d | PDV + caixa: venda de balcão com pagamento dividido, caixa com conferência, venda x fechamento serializados por trava | ✅ |
| 6e | Pedido personalizado: orçamento versionado com máquina de estados, link com token, conversão única, concorrência por trava | ✅ |
| 6f | Substituição com consentimento: modo do pedido congelado, tolerância da política, link com prazo, aplicação única com estoque travado | ✅ |
| 6g | Observabilidade: OpenTelemetry (traces que atravessam a outbox, métricas de negócio, saúde da fila), `X-Trace-Id`, Aspire Dashboard local, ADOT na AWS | ✅ |
| 7 | Produção na AWS com CDK (C#): ECS Fargate api/worker/migrator, RDS Multi-AZ, Secrets Manager, ALB + WAF, alarmes, deploy via GitHub OIDC. Ver [docs/aws.md](docs/aws.md) | ✅ (pronta, não implantada) |
