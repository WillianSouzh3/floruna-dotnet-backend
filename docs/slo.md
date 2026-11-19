# SLOs do Floruna

Objetivos de nível de serviço, medidos pela telemetria (ADR 0014) e verificados no teste de carga (`load/`).
Janela de 30 dias. O **orçamento de erro** é o que sobra até violar o objetivo: enquanto houver orçamento, o time
entrega novidade; esgotado, a prioridade vira confiabilidade.

| SLO | Objetivo | Medida | Orçamento (30 dias) |
|---|---|---|---|
| Disponibilidade da API | 99,5% das requisições sem erro 5xx | ALB `HTTPCode_Target_5XX` / total | ~3 h 36 min de indisponibilidade equivalente |
| Latência da vitrine | p95 < 150 ms | `http.server.request.duration` (rotas de catálogo) | 5% das requisições acima |
| Latência do checkout | p95 < 800 ms | `floruna.request.duration{request=PlaceOrderCommand}` | 5% acima |
| Frescor da consistência eventual | p99 da idade da outbox < 60 s | `floruna.outbox.oldest_pending_age` | 1% do tempo acima |
| Correção do estoque | **zero** oversell | teste de carga + invariantes no banco (`CHECK`) | sem orçamento: é invariante, não meta |

Alarmes ligados no CDK (ADR 0011): 5xx, p95 de latência, hosts não saudáveis, CPU da API e do banco, disco do banco.
Alarmes de outbox: backlog da ADR 0014 (dependem do nome final da métrica EMF no primeiro deploy).
