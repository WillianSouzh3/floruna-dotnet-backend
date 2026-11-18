# ADR 0001 — Fundação da reescrita em .NET

- **Status:** aceita
- **Data:** 2026-11-18

## Contexto

O backend do Floruna é NestJS 11 + MongoDB (~85 mil linhas, 52 módulos). A
reescrita em C# serve a dois objetivos: sistema profissional e material de
estudo/entrevista. O front Next.js (BFF) deve continuar funcionando.

## Decisões

1. **.NET 10 LTS**, Clean Architecture em 4 camadas + SharedKernel, monolito modular.
2. **PostgreSQL + EF Core.** Unit of Work nativo, constraints (`CHECK qty >= 0`), SQL para relatórios.
3. **Controllers** (não Minimal APIs), por serem o padrão mais cobrado em vagas.
4. **Mediator próprio** (`Floruna.Application/Messaging`), para estudo. Mesma API conceitual do MediatR
   (que passou a ter licença comercial), então a troca futura é mecânica.
5. **Contrato HTTP idêntico ao Nest:** `/api/v1`, envelope `{data, meta}` / `{error:{code,message,issues}, meta}`,
   `X-Request-ID`, `X-API-Version`, porta 4000, JWT HS256 com o mesmo `AUTH_SECRET`.
6. **Result em vez de exceção** para regras de negócio; exceção só para o inesperado.
7. **Dinheiro em centavos** (`Money`, `long`). Arredondamento de porcentagem `AwayFromZero` (igual ao `Math.round` do JS para positivos).
8. **Shouldly** em vez de FluentAssertions (v8 comercial).
9. **AWS** como nuvem-alvo (ECS Fargate, RDS PostgreSQL, Secrets Manager), ver §19 do plano.

## Consequências

- A regra de dependência é verificada por `Floruna.ArchitectureTests` (NetArchTest).
- O contrato HTTP é verificado por `Floruna.Api.IntegrationTests`. Quebrar um teste = quebrar o front.
- Migração de dados Mongo → Postgres exigirá ETL por loja (Fase de migração, §16 do plano).
