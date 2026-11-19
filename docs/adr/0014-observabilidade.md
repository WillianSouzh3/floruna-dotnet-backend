# ADR 0014 — Observabilidade com OpenTelemetry (Fase 6g)

- **Status:** aceita
- **Data:** 2026-11-19

## Decisões

1. **Camadas internas só com o BCL**: `ActivitySource` e `Meter` (`System.Diagnostics`). O pacote OpenTelemetry
   entra apenas na API (borda), que decide o que exportar e para onde. Trocar de fornecedor é configuração.
2. **Coleta sempre ligada, exportação opcional**: com `OTEL_EXPORTER_OTLP_ENDPOINT` definido, vai por OTLP
   (Aspire Dashboard local; coletor ADOT na AWS → X-Ray e CloudWatch). Sem ele, nada sai.
3. **Traces**: ASP.NET Core, HttpClient (gateway), Npgsql e um span por comando/consulta do mediator (no
   `LoggingBehavior`, que já era o decorator mais externo). Erro de NEGÓCIO (sem estoque, cupom esgotado) não marca
   o span como erro: vira tag (`floruna.error.code`); exceção marca.
4. **O trace atravessa a outbox**: o `traceparent` W3C é gravado na mensagem, na mesma transação do evento; o
   worker abre o span de consumo com esse pai. O e-mail enviado minutos depois aparece no MESMO trace do checkout.
   Teste de integração (e de mutação: sem gravar o `traceparent`, falha).
5. **Correlação**: toda resposta leva `X-Trace-Id` (o suporte pede esse código ao cliente); logs JSON com
   `TraceId`/`SpanId` e a loja no escopo (RNF-08); o span HTTP leva `floruna.tenant` e `floruna.request_id`.
6. **Métricas de negócio** (centavos, tags de baixa cardinalidade): pedidos e valor por origem (online/pdv),
   pagamentos confirmados por fonte, reembolsos por desfecho, mensagens da outbox por desfecho, contagem e duração
   de comandos por desfecho. **Saúde da outbox** (RNF-04): idade da pendente mais antiga, pendentes e mortas. O
   worker amostra uma vez por ciclo; o callback do gauge só lê (callback de gauge não faz I/O).
7. **Amostragem que corta ruído**: span de SAÍDA (banco, HTTP) sem pai é descartado. Os jobs consultam o banco a
   cada 2 s; sem isso, cada consulta "vazia" seria um trace (medido: dezenas de spans em 20 s ociosos → zero; a
   requisição real continua inteira).
8. **AWS**: sidecar ADOT (`aws-otel-collector`) não essencial em `api` e `worker` (telemetria caindo não derruba
   vendas), com permissões de X-Ray e de métricas EMF restritas ao prefixo `/aws/ecs/application/metrics`.
9. **Local**: perfil `observability` no compose com o Aspire Dashboard (o que o plano previa para o RNF-08).

## Verificação

- Testes de integração: trace ponta a ponta pela outbox, métricas de pedidos/recusas, gauges da outbox,
  cabeçalhos de correlação.
- Exportação real: a imagem, com um OpenTelemetry Collector (exporter `debug`), recebeu
  `GET api/v1/products → ListProductsQuery → postgresql` com `service.name`, loja e request id.

## Lição

O teste dos gauges passava sozinho e falhava na suíte: cada variação do `WebApplicationFactory` é um host com
o seu `Meter` "Floruna". O teste passou a filtrar pelo ESCOPO do medidor (o `IMeterFactory` daquele host).
Em produção é o mesmo princípio: métrica por processo, agregada no backend.

## Backlog

- Alarme no CloudWatch para `floruna.outbox.oldest_pending_age > 60 s` e `dead_letters > 0` (depende do nome final
  da métrica EMF publicada pelo coletor; confirmar no primeiro deploy).
- Logs também via OTLP (hoje: JSON no stdout → CloudWatch Logs, correlacionados pelo `TraceId`).
- Amostragem por cauda (tail sampling) no coletor: guardar 100% dos traces com erro e uma fração dos demais.
