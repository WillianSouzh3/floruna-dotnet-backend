# Floruna .NET na VPS da Hostinger

Produção roda na VPS KVM da Hostinger (`deploy@2.24.31.169`, x86_64, 2 vCPU, 7,7 GB), **compartilhada** com
outros sistemas: economize (o Caddy dele é a borda HTTPS de todos), casa-nativa, portal-do-conhecimento,
watchparty, forma e o flora-lis (Nest + Next.js). A AWS (`infra/`, [aws.md](aws.md)) fica como material de estudo.

## Como fica

```
VPS
├── flora-lis_default (rede do flora-lis)
│   ├── web (Next.js/BFF) ──► api:4000 (Nest)          ← tráfego de hoje
│   │                     └─► floruna-api:4000 (.NET)  ← só lojas em DOTNET_TENANT_HOSTS
│   └── floruna-api  (API .NET; o serviço NÃO pode se chamar `api`: ver o compose)
└── floruna-dotnet_default (rede própria)
    ├── floruna-api (.NET: HTTP + jobs, mem 768 MB)
    ├── db       (PostgreSQL 17, sem porta publicada, mem 512 MB)
    ├── backup   (pg_dump a cada 6 h, 7 dias)
    └── backup-sync (cópia no mesmo S3 do backup do flora-lis, prefixo floruna-dotnet/)
```

Nada é publicado na internet. Enquanto o BFF não tiver `DOTNET_API_URL`, o .NET não recebe nenhuma requisição.
Arquivos: [`deploy/vps/docker-compose.prod.yml`](../deploy/vps/docker-compose.prod.yml) e
[`.github/workflows/deploy-vps.yml`](../.github/workflows/deploy-vps.yml).

## Setup único (manual)

Feito por uma pessoa, porque mexe em credenciais: copia o `AUTH_SECRET` e as chaves do S3 do `.env` do flora-lis
e cria a chave SSH exclusiva do CI.

### 1. Pasta e `.env` na VPS

```bash
ssh deploy@2.24.31.169 'bash -s' <<'EOF'
set -euo pipefail
umask 077
mkdir -p ~/floruna-dotnet && cd ~/floruna-dotnet
[ -f .env ] && { echo ".env já existe: nada alterado."; exit 0; }
random() { tr -dc 'A-Za-z0-9' < /dev/urandom | head -c "$1"; }
{
  echo "POSTGRES_PASSWORD=$(random 40)"
  echo "ENCRYPTION_KEY=$(random 64)"
  # AUTH_SECRET igual ao do flora-lis: a sessão vale nos dois backends durante a migração.
  grep -E '^(AUTH_SECRET|PLATFORM_DOMAIN|BACKUP_S3_BUCKET|AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY|S3_REGION|S3_ENDPOINT)=' ~/flora-lis/.env
} > .env
chmod 600 .env
cut -d= -f1 .env | tr '\n' ' '; echo
EOF
```

Guarde o `ENCRYPTION_KEY` num cofre: sem ele, os tokens de gateway das lojas não se decifram (backup do banco
não basta).

### 2. Chave SSH do CI e secrets do repositório

```bash
ssh-keygen -t ed25519 -N "" -C floruna-dotnet-ci -f /tmp/floruna-ci
ssh deploy@2.24.31.169 'cat >> ~/.ssh/authorized_keys' < /tmp/floruna-ci.pub
gh secret set VPS_HOST    -R WillianSouzh3/floruna-dotnet-backend -b 2.24.31.169
gh secret set VPS_USER    -R WillianSouzh3/floruna-dotnet-backend -b deploy
gh secret set VPS_SSH_KEY -R WillianSouzh3/floruna-dotnet-backend < /tmp/floruna-ci
shred -u /tmp/floruna-ci /tmp/floruna-ci.pub
```

### 3. Ligar e rodar o primeiro deploy

```bash
gh variable set VPS_DEPLOY_ENABLED -R WillianSouzh3/floruna-dotnet-backend -b true
gh workflow run deploy-vps.yml -R WillianSouzh3/floruna-dotnet-backend
```

Daí em diante, todo push na `main` com CI verde implanta sozinho.

## O que o deploy faz

1. Valida o `.env` (nada é tocado se faltar algo).
2. Login no ghcr.io com o token efêmero do job (nenhuma credencial fica no servidor) e `pull` da imagem nova.
3. Sobe o banco e roda as **migrations com a imagem nova**, antes de trocar a API.
4. Troca a API e faz a fumaça (`/api/v1/health/ready`, de um container curl na mesma rede).
5. Fumaça falhou: volta a tag anterior e o job fica vermelho.
6. Sobe o backup e apaga imagens antigas **só deste projeto** (mantém as 3 últimas).

## Operação

```bash
ssh deploy@2.24.31.169
cd ~/floruna-dotnet
docker compose -f docker-compose.prod.yml ps
docker compose -f docker-compose.prod.yml logs -f --tail 100 floruna-api      # logs em JSON
docker compose -f docker-compose.prod.yml exec db psql -U floruna       # banco
```

Restaurar um backup: `docker compose -f docker-compose.prod.yml exec -T db pg_restore -U floruna -d floruna --clean < arquivo.dump`.

Remover tudo (o flora-lis não depende disto): `docker compose -f docker-compose.prod.yml --profile backup down`
(acrescente `-v` só se quiser apagar o banco também).

## Ligar uma loja no .NET

Depois do ETL ([migracao.md](migracao.md)), no `.env` do flora-lis (BFF):

```
DOTNET_API_URL=http://floruna-api:4000/api/v1
DOTNET_TENANT_HOSTS=flora-lis.floruna.com.br,www.floraelis.com.br
```

e reinicie o container `web`. Webhook do Mercado Pago para pagamentos NOVOS dessas lojas: rota pública para o
.NET no Caddy/nginx (passo do runbook de migração).
