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
   Verificado com `jose` (a biblioteca do `proxy.ts`) em Node, nos dois sentidos.
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

## Complementos da Fase 1 (implementados)

9. **Bloqueio progressivo de login** (`LoginLockoutPolicy`, igual ao Nest): conta+IP a partir de 5 falhas,
   conta de qualquer IP a partir de 10, janela de 24 h. Contadores em `login_attempts` com UPSERT atômico do
   PostgreSQL e chaves HMAC (sem e-mail/IP em claro). Resposta 429 `RATE_LIMITED` + `Retry-After`.
10. **Credencial de uso único** (`SingleUseToken`) para 2FA (10 min), redefinição de senha (30 min) e
    impersonação (60 s): só o hash é guardado; consumo por compare-and-swap; presa à loja.
11. **2FA por e-mail** ligado quando há SMTP (mesma regra do Nest). O token pendente tem `purpose=2fa` e é
    recusado como sessão.
12. **E-mail em fila de background** (`Channel` + `BackgroundService`, Queue-Based Load Leveling): o request não
    espera o SMTP e o "esqueci minha senha" responde no mesmo tempo com ou sem conta. Fila em memória:
    vira outbox na Fase 4.
13. **Equipe** com teto de usuários do plano efetivo, regras de "último master" (`TeamPolicy`) e auditoria.
14. **Superadmin** navega por todas as lojas (sessão validada na loja do token, via `IgnoreQueryFilters` com loja
    explícita, só em `StaffUserRepository`). **Impersonação**: código de 60 s trocado no host da loja por sessão
    de master de 15 min com `impersonatedBy`, auditada na loja afetada.
15. **Domínio próprio** com prova de posse por TXT (`_flora-verify.{apex}`). Índice único só entre domínios
    VERIFICADOS (um pedido pendente não bloqueia o dono real).
16. **IP real atrás do ALB**: `ForwardedHeaders` só confia em `Network:TrustedProxies` (faixas da VPC).

### Bug encontrado na regra herdada

A resolução de host remove `www.`, mas o domínio é guardado como digitado (`www.loja.com.br`). No Nest, essa
combinação aparenta impedir que um domínio cadastrado com `www` roteie (não verificado em produção). Aqui a
consulta procura as duas formas, coberto por teste de integração.

## Ainda não implementado

- Grant de pareamento do app móvel (QR code), capabilities finas (`@RequireCapability`), idioma do painel.
- Endpoint `tls-allowed` (emissão de certificado sob demanda): na AWS, o TLS de domínio próprio vai para ACM/CloudFront (Fase 7).
- Limpeza periódica de `login_attempts` e `single_use_tokens` vencidos (job da Fase 4).
- Rate limit distribuído (Redis) e regra rate-based no AWS WAF (Fase 7).

## Consequências

- `TenantIsolationTests` prova o isolamento direto na persistência, e falha se uma entidade de loja ficar sem filtro.
- `EnableRetryOnFailure` está ligado: quando houver transação explícita (Fase 4), ela precisa rodar dentro da
  *execution strategy* do EF.
- Migrations na subida só em dev/teste. Em produção: `dotnet ef migrations bundle` como etapa do deploy.
