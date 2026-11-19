# ADR 0011 — Produção na AWS (Fase 7)

- **Status:** aceita
- **Data:** 2026-11-19

Guia completo (arquitetura, runbooks, custos): [docs/aws.md](../aws.md).

## Decisões

1. **CDK em C#** (`infra/`), mesma linguagem do sistema. Projeto isolado (net8.0, `RollForward=Major`): não herda
   o `Directory.Build.props` da API. Requer Node.js (jsii).
2. **ECS Fargate** em vez de EKS (sem cluster para operar) ou Lambda (API com conexões longas ao PostgreSQL,
   jobs contínuos e pool de conexões). **RDS PostgreSQL** em vez de Aurora: menor custo de entrada; Aurora
   Serverless v2 é a evolução se a carga for muito variável.
3. **Uma imagem, três papéis** (api, worker, migrator) pela configuração `BackgroundJobs:Enabled` e pelo
   comando `migrate`. Bulkhead entre atender HTTP e processar jobs.
4. **Migrations antes da app**, numa pilha própria (`Migration`). Pipeline: infra → migrator (exit 0) → app.
   Migrations compatíveis com a versão anterior (expand/contract). O EF Core trava a tabela de histórico:
   dois migrators simultâneos não se atropelam.
5. **Segredos**: gerados pelo Secrets Manager e injetados pelo ECS. A chave de cifra virou "frase aleatória
   de 64 caracteres" (o Secrets Manager não gera "32 bytes em base64"); a app deriva a chave AES com HKDF-SHA256
   (`EncryptionKeyMaterial`), mantendo compatível o formato base64 antigo. O banco chega campo a campo e a app
   monta a connection string com o builder do Npgsql (escapa caracteres especiais, TLS obrigatório).
6. **Rede em três camadas**, banco em subnet isolada. SGs todos na pilha de rede (evita ciclo entre pilhas).
   `X-Forwarded-For` só é confiável a partir da faixa da VPC (rate limit e bloqueio de login usam o IP real).
7. **Borda**: ALB com TLS 1.2+, redirect 80→443, `drop_invalid_header_fields`, access logs em S3; WAF com regras
   gerenciadas (Common, KnownBadInputs, SQLi) e limite por IP. A regra de tamanho de corpo do Common fica em
   *count* (descrições de produto passam de 8 KB).
8. **Resiliência**: Multi-AZ, circuit breaker com rollback, autoscaling por CPU e requisições (sobe em 60 s,
   desce em 300 s), health check de prontidão com o banco, alarmes de sintoma (5xx, p95) e de causa (CPU, disco).
9. **ARM64/Graviton** em Fargate e RDS (custo e eficiência).
10. **Deploy sem chave de acesso**: GitHub OIDC → role que só aceita este repositório (branch `main` ou
    ambientes do GitHub; `prod` com aprovação manual) e só assume as roles do bootstrap do CDK.
11. **A infra é testada como código**: asserções no CloudFormation gerado (segurança, papéis, escala),
    cdk-nag (AwsSolutions) sem achado sem justificativa, e "synth determinístico" (sem lookup sem contexto).
    Cada supressão do cdk-nag tem motivo, e os testes de proteção foram validados por mutação.

## Lições desta fase

- **Referência circular entre pilhas**: regra de SG "banco aceita app" com os SGs em pilhas diferentes fez a
  pilha de dados depender da de app e vice-versa. Solução: SGs na pilha de rede.
- **Synth "verde" que quebra no CLI**: AZs explícitas numa pilha com conta fixa disparam lookup de contexto. O
  synth em processo só anota "contexto faltando"; quem chama a AWS (e falha sem credencial) é o CLI. Teste
  novo: o manifesto não pode ter contexto faltando; `cdk.context.json` versionado.
- **Contrato do container testado com a imagem real**: migrator em banco vazio (10 migrations), idempotência,
  API com configuração por campos e chave no formato do Secrets Manager, logs JSON.

## Backlog

Ver a seção 8 de [docs/aws.md](../aws.md): cache distribuído (Redis), rotação de senha, TLS `VerifyFull`,
Data Protection compartilhado, comando de seed seguro, métricas da outbox, tracing, CloudFront, multi-região.
