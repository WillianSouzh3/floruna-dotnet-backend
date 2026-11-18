# ADR 0002 — Multi-tenancy e autenticação (Fase 1)

- **Status:** aceita
- **Data:** 2026-11-18

## Contexto

Um banco compartilhado entre lojas. O risco nº 1 é esquecer o filtro da loja numa
consulta. O Nest resolve com um plugin do Mongoose + AsyncLocalStorage que falha
fechado. O front Next.js precisa continuar validando a sessão (`proxy.ts`, `jose`).

## Decisões

1. **Loja por requisição em serviço scoped** (`TenantContext`), preenchido por middleware
   a partir de `x-tenant-host` (ou `Host`). Regras de host idênticas ao Nest; cache de 60 s.
2. **Filtro global do EF Core** em toda entidade `ITenantOwned` + **interceptor** que carimba o
   `TenantId` na inserção. Sem loja no contexto, consultar ou gravar lança
   `TenantContextMissingException` (**fail-closed**). O payload nunca escolhe a loja.
3. **Cadastro de loja numa transação** (loja + usuário master no mesmo `SaveChanges`).
   O Nest criava em dois passos com compensação manual.
4. **JWT HS256 com os claims do Nest** (`sub, email, name, role, tenantId, sessionVersion, locale`).
   Verificado com `jose` e `jsonwebtoken`-style signing em Node, nos dois sentidos.
5. **BCrypt custo 10** (`BCrypt.Net-Next`), compatível com `bcryptjs`: senhas migradas funcionam.
6. **Autorização segura por padrão**: `MapControllers().RequireAuthorization()`; rotas públicas usam `[AllowAnonymous]`.
7. **Recurso por plano** via `[RequiresPlanFeature]` (`IAuthorizationRequirementData`), código `PLAN_UPGRADE_REQUIRED`.
8. **Rate limiting nativo** (janela fixa por loja + IP) em login, cadastro e signup.

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET | Motivo |
|---|---|---|---|
| Checagem de `sessionVersion` | só `superadmin/master/gerente` | todo papel de equipe, incluindo `caixa` | revogar sessão do caixa também precisa funcionar |
| Slug com 1–2 caracteres | aceito pela regex | recusado (3–40) | a própria mensagem do Nest diz 3–40 |
| Signup | 2 passos + compensação | 1 transação | atomicidade garantida pelo banco |

## Ainda não implementado (backlog da Fase 1)

- 2FA por e-mail, bloqueio progressivo por tentativas (`AuthAttempt`), "esqueci minha senha" (RF-IAM-05).
- Gestão de usuários da equipe com teto por plano (RF-IAM-06), `GET /tenants/me`.
- Superadmin entre lojas e impersonação auditada; domínio próprio com verificação DNS.
- Rate limit distribuído (Redis) e regra rate-based no AWS WAF.

## Consequências

- `TenantIsolationTests` prova o isolamento direto na persistência, e falha se uma entidade de loja ficar sem filtro.
- `EnableRetryOnFailure` está ligado: quando houver transação explícita (Fase 4), ela precisa rodar dentro da
  *execution strategy* do EF.
- Migrations na subida só em dev/teste. Em produção: `dotnet ef migrations bundle` como etapa do deploy.
