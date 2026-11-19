# ADR 0016 — Migração do Nest: ETL por loja e Strangler Fig no BFF

- **Status:** aceita
- **Data:** 2026-11-19

## Contexto

O plano (§16) previa o Strangler Fig por MÓDULO (ex.: só o catálogo no .NET). Com dois bancos (Mongo e
PostgreSQL), isso não fecha: catálogo num banco e checkout no outro significa estoque vendido num lado e disponível
no outro. Módulos de uma mesma loja compartilham dados.

## Decisões

1. **A unidade de corte é a LOJA.** O ETL copia a loja inteira; o BFF passa a mandar TODO o tráfego dela para o
   .NET (`DOTNET_TENANT_HOSTS`). Só módulos independentes dos dados da loja ficam no Nest (`NEST_ONLY_MODULES`,
   padrão `i18n`). Lojas não migradas não percebem nada. Sem `DOTNET_API_URL`, o BFF se comporta como antes.
2. **Elegibilidade automática**: o ETL recusa loja com pedido pendente ou com dado operacional em módulo que ele não
   importa. Configuração com valor padrão (perfil fiscal, política de substituição) só gera aviso.
3. **As fábricas do domínio validam o dado legado**; um restaurador (reflection, só na ferramenta) copia o que é
   FATO passado (id, datas, estado). O domínio não ganha "construtores de importação". Registro recusado pelas
   regras de hoje bloqueia o corte (sem `--force`): não se perde dado em silêncio.
4. **IDs determinísticos** (UUID v5 do ObjectId): referências se resolvem sem tabela de-para e os ensaios geram as
   mesmas chaves.
5. **Uma transação por loja + conferência antes do commit**: contagens, unidades em estoque e soma dos pedidos em
   centavos. Divergiu, nada é gravado. `--dry-run` faz tudo e desfaz.
6. **Continuidade para quem usa**: hash BCrypt do Nest aceito (ninguém troca senha), link de acompanhamento do
   pedido preservado (o token em claro do Nest vira hash SHA-256), token do gateway decifrado com a chave do Nest e
   recifrado com a nova, lotes com validade (FEFO preservado), pedidos pagos reembolsáveis.
7. **Eventos de domínio do histórico são descartados**: importar 2 mil pedidos não manda 2 mil e-mails.

## O que a conferência pegou durante o desenvolvimento

- `BsonDocument.Merge` ALTERA o documento base: o filtro de lotes grudou no filtro da loja e a busca de produtos
  voltou vazia. A migração saiu com 0 produtos e a conferência recusou gravar.
- Um produto recusado (variação com id fora do padrão) deixava o estoque divergente: a conferência barrou; a política
  virou "recusa bloqueia, `--force` desconta exatamente o recusado".
- Chaves únicas da PLATAFORMA (código de indicação, id de variação) podem colidir entre lojas: tratadas.

## Verificação

- 7 testes ponta a ponta: Mongo (Testcontainers) semeado no formato dos schemas do Nest, hash gerado pelo próprio
  `bcryptjs` do Nest, segredo cifrado no formato `enc:v1` do Nest. Depois do ETL, tudo é conferido PELA API .NET.
- CLI real contra Mongo e PostgreSQL em containers: ensaio, gravação, login com a senha antiga pela API.
- BFF: 7 testes de roteamento; suíte inteira (406 testes), typecheck e lint verdes.

## Consequências

- Reversão só é limpa antes de reabrir a loja (não há ETL reverso). O runbook exige fumaça antes de reabrir.
- Equipe e clientes entram de novo no corte (o `tenantId` dos tokens do Nest é o ObjectId).
- Webhooks de pagamentos anteriores ao corte chegam no Nest: manter o Nest no ar, só leitura, por um período.
