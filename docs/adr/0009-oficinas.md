# ADR 0009 — Oficinas e eventos (Fase 6c)

- **Status:** aceita
- **Data:** 2026-11-19

## Decisões

1. **Mesmo contrato do Nest** (`/events`, `event`, `registrations`), com o agregado chamado `Workshop` no domínio.
2. **Vaga atômica com UM comando**: `UPDATE workshops SET registered = registered + 1 WHERE id = ? AND is_active AND
   registered < capacity`. Condição e incremento são avaliados juntos pelo banco, com a linha travada durante o
   UPDATE: não existe "li 9, gravei 10". Concorrência otimista ou "ler, checar, gravar" perderiam aqui.
3. **Vaga e inscrição na MESMA transação.** O Nest reserva a vaga, grava a inscrição e, se falhar, devolve a vaga
   (compensação, que também pode falhar). No PostgreSQL não há janela: ou as duas coisas, ou nenhuma. 30 inscrições
   simultâneas para 10 vagas: 10 inscrições, contador 10 (teste).
4. **Segunda barreira no banco**: `CHECK (registered <= capacity)`. Mesmo um bug não vende a 11ª vaga.
5. **Mudar a capacidade trava a linha** (`FOR UPDATE`): a regra "capacidade ≥ inscritos" não é furada por uma inscrição
   concorrente entre a checagem e o commit. O UPDATE do EF só grava as colunas alteradas, então a edição não pisa no
   contador.
6. **Remover oficina com inscritos é bloqueado** (domínio + FK `Restrict`): "desative em vez de remover".

## Diferenças conscientes em relação ao Nest

| Item | Nest | .NET |
|---|---|---|
| `GET /events` público | lista também as desativadas (exceto com `onlyActive=true`) | visitante só vê ativas; equipe vê todas |
| `GET /events/:slug` de oficina desativada | devolve | 404 para visitante |
| Remover oficina com inscritos | apaga (inscrições órfãs) | 409 |
| Inscrição | reserva + compensação | uma transação |

## Backlog

- Cobrança da inscrição em oficinas pagas (PIX, reaproveitando `PaymentIntent`), com reserva de vaga por prazo.
- Cancelamento de inscrição (devolve a vaga), lista de espera e lembrete por e-mail (outbox).
