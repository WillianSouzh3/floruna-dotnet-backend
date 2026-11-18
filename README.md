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
  Floruna.Api.IntegrationTests    contrato HTTP (envelope, erros, headers, CORS, health)
```

## Rodando

Requer o **.NET 10 SDK** (`sudo apt install dotnet-sdk-10.0`) e Docker.

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

## Configuração (Options Pattern, validada na subida)

| Chave | Variável de ambiente | Exemplo |
|---|---|---|
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0` | `http://localhost:3000` |
| `GIT_SHA` | `GIT_SHA` | SHA do commit (preenchido pelo CI na imagem) |

Configuração inválida impede a API de subir (fail-fast).

## Roadmap

| Fase | Conteúdo | Status |
|---|---|---|
| 0 | Fundação: solution, mediator, envelope, Options, health, CI, testes de arquitetura | ✅ |
| 1 | Plataforma + Identity: tenant por host, query filter fail-closed, JWT compatível, planos | ⏳ |
| 2 | Catálogo | |
| 3 | Estoque (lotes FEFO, ledger, reservas) | |
| 4 | Checkout + Pedido + Outbox | |
| 5 | Pagamentos (PIX, Mercado Pago) | |
| 6 | Fidelidade, oficinas, PDV, orçamentos | |
| 7 | Produção na AWS (ECS Fargate, RDS, Secrets Manager, CDK) | |
