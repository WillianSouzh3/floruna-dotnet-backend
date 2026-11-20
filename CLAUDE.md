# CLAUDE.md

## Codebase Overview

Backend multi-tenant de e-commerce para floriculturas. É a reescrita em .NET 10 do backend NestJS + MongoDB (`../flora-lis-site/api`) e mantém o **contrato HTTP idêntico** (`/api/v1`, envelope `{data,meta}`/`{error,meta}`, JWT HS256, header `x-tenant-host`) para reutilizar o front Next.js. Monólito modular com Clean Architecture, DDD, CQRS e **Mediator próprio** (sem MediatR).

**Stack**: .NET 10, ASP.NET Core Controllers, EF Core + PostgreSQL 17, FluentValidation, OpenTelemetry, Mercado Pago (PIX), xUnit + Shouldly + NSubstitute + Testcontainers, AWS CDK em C# (ECS Fargate + RDS, sa-east-1).

**Structure**: `src/` com `SharedKernel ← Domain ← Application ← Infrastructure ← Api` (+ `Migration`, o ETL Mongo→Postgres por loja); `tests/` com 4 projetos; `infra/` com CDK (solução separada, net8.0); `docs/` com plano, ADRs 0001–0016 e o snapshot OpenAPI.

Para a arquitetura detalhada, convenções, armadilhas e guia de navegação, veja [docs/CODEBASE_MAP.md](docs/CODEBASE_MAP.md).

## Regras que quebram o build/teste

- `TreatWarningsAsErrors` + estilo no build. O CI roda `dotnet format --verify-no-changes`.
- Nunca use `DateTime.UtcNow/Now` em `src/`: use o `TimeProvider` injetado.
- Nada de `double`/`float` em Domain/SharedKernel: dinheiro é `long` em centavos.
- Handlers devem ser `internal sealed`.
- `IgnoreQueryFilters` só é permitido nos arquivos da allowlist de `tests/Floruna.ArchitectureTests/SourceRulesTests.cs`.
- Mudou rota/DTO? Rode `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test` e revise `docs/openapi/v1.json`.
- Classes de teste de integração precisam de `[Collection(ApiCollection.Name)]` e devem criar a própria loja.

## Comandos

```bash
docker compose up --build                     # API :4000, docs /api/docs, Postgres :5433
dotnet test tests/Floruna.Domain.UnitTests    # rápido, sem Docker
dotnet test Floruna.slnx                      # suíte completa (Docker necessário)
```

O SDK local pode ser só o 8.0. Nesse caso, compile no container `mcr.microsoft.com/dotnet/sdk:10.0` ou instale o `dotnet-sdk-10.0`.
