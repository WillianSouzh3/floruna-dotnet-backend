# Teste de carga: Dia das Mães

`mothers-day.js` (k6) simula o pico com dois públicos simultâneos:

- **browse**: vitrine (60% listagem, 40% detalhe);
- **checkout**: compras, 70% no MESMO buquê disputado, de estoque limitado; o resto em 12 arranjos com estoque farto.

Verifica latência (RNF-01), erro (< 1%; 201 e 409 "sem estoque" são esperados) e **oversell zero** (RNF-03): no fim,
o buquê disputado nunca tem mais reservado do que o estoque inicial.

## Como rodar

```bash
CHECKOUT_RATE_LIMIT=1000000 docker compose up --build -d   # o k6 manda tudo de um IP: o rate limit de checkout sobe só aqui
docker run --rm -i --network host -e BASE_URL=http://localhost:4000 \
  -e PEAK_BROWSE=150 -e PEAK_CHECKOUT=30 -e HOT_STOCK=300 -e HOLD=60s \
  -v "$PWD/load:/load" grafana/k6 run /load/mothers-day.js
```

Variáveis: `PEAK_BROWSE` (req/s na vitrine), `PEAK_CHECKOUT` (checkouts/s), `HOT_STOCK` (estoque do buquê disputado),
`HOLD` (tempo no pico; a rampa é de 20 s e a descida de 10 s).

## Resultados (2026-11-19)

Notebook de 4 núcleos e 8 GB, com k6, API (1 instância) e PostgreSQL na MESMA máquina, via `docker compose`.
Indicativo, não é capacidade de produção: em produção a API escala horizontalmente e o banco tem máquina própria.

| Rodada | Pico (vitrine + checkout) | Vazão sustentada | p95 vitrine | p95 checkout | Falhas | Buquê disputado |
|---|---|---|---|---|---|---|
| 1 | 150 + 30 req/s, 30 s | 133 req/s | 16 ms | 54 ms | 0% | 300 de 300 reservados |
| 2 | 400 + 100 req/s, 60 s | 415 req/s | 11 ms | 36 ms | 0% | 500 de 500 reservados |
| 3 (estresse) | 1000 + 250 req/s, 30 s | ~730 req/s (o resto descartado pelo k6) | 629 ms | 1,85 s | 0% | 500 de 500 reservados |

Leitura:

- **Até ~400 req/s**, folga grande nos dois SLOs (RNF-01: vitrine < 150 ms, checkout < 800 ms).
- **No estresse** a máquina saturou (API ~150% de CPU, PostgreSQL ~135%, o resto com o k6): a latência estoura,
  mas **nenhuma requisição falhou e nenhuma unidade foi vendida a mais**. Sob sobrecarga o sistema fica lento,
  nunca incorreto. É a propriedade que importa no Dia das Mães; latência se resolve escalando (autoscaling).
- Os 409 crescem com a disputa: depois que o buquê esgota, a recusa é barata (a trava da linha decide rápido).

## No CI

`.github/workflows/load.yml` roda o perfil leve (150 + 30 req/s) toda noite e sob demanda; os limites do script
quebram o job se a latência ou a correção regredirem.
