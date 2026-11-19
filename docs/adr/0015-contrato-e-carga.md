# ADR 0015 — Contrato versionado e teste de carga

- **Status:** aceita
- **Data:** 2026-11-19

## Decisões

1. **Snapshot do contrato** (RNF-10): `docs/openapi/v1.json` é versionado. Um teste de integração gera o
   `openapi.json` da API, normaliza (chaves ordenadas, sem `servers`) e compara. Qualquer diferença quebra o build
   com um resumo: operações **removidas** aparecem como QUEBRA DE CONTRATO, as novas como adições. Aceitar é um ato
   deliberado (`UPDATE_OPENAPI_SNAPSHOT=1`), e o diff do arquivo vai para a revisão do PR. Outro teste exige que
   toda operação documente suas respostas.
   Validado por mutação: renomear uma rota acusa a remoção da antiga.
2. **Rotas de teste fora do contrato**: o controlador de sonda dos testes vazava 7 rotas `/_probe/*` para o
   snapshot. Agora tem `[ApiExplorerSettings(IgnoreApi = true)]`.
3. **Paridade com o Nest** (`tools/api_parity.py` → `docs/paridade-api.md`): compara método + caminho dos dois
   `openapi.json`, por módulo. Guia do Strangler Fig: módulo a 100% pode ser roteado para a API .NET no BFF.
   Situação em 2026-11-19: 115 de 282 operações (40%); 13 módulos completos.
4. **Teste de carga "Dia das Mães"** (k6, `load/`): vitrine e checkout simultâneos, 70% das compras no mesmo
   buquê de estoque limitado. Limites no script: p95 da vitrine < 150 ms, do checkout < 800 ms, erro < 1%, e
   **oversell zero** verificado no fim. Roda toda noite no CI (perfil leve).
5. **SLOs** documentados com orçamento de erro (`docs/slo.md`).

## Resultado

Num notebook de 4 núcleos com tudo junto (k6, API, banco): ~415 req/s sustentados com p95 de 11 ms (vitrine) e
36 ms (checkout); no estresse (~730 req/s), a latência estoura, mas **zero falhas e zero oversell**. Sob sobrecarga
o sistema fica lento, nunca incorreto. Detalhes em `load/README.md`.
