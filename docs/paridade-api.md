# Paridade de contrato: Nest x .NET

Gerado por `tools/api_parity.py` a partir dos dois `openapi.json` (método + caminho; parâmetros normalizados).
Guia do Strangler Fig: um módulo com 100% pode ter o tráfego roteado para a API .NET no BFF.

**Cobertura geral: 115 de 282 operações do Nest (40%).** A API .NET tem 7 operações que o Nest não tem (rotas novas ou desvios documentados nas ADRs).

| Módulo | Nest | Já no .NET | % | Situação |
|---|---:|---:|---:|---|
| `cash-sessions` | 5 | 5 | 100% | pronto para rotear |
| `categories` | 6 | 6 | 100% | pronto para rotear |
| `collections` | 8 | 8 | 100% | pronto para rotear |
| `custom-orders` | 6 | 6 | 100% | pronto para rotear |
| `custom-quotes` | 4 | 4 | 100% | pronto para rotear |
| `delivery-capacity` | 4 | 4 | 100% | pronto para rotear |
| `delivery-promises` | 1 | 1 | 100% | pronto para rotear |
| `delivery-zones` | 5 | 5 | 100% | pronto para rotear |
| `events` | 7 | 7 | 100% | pronto para rotear |
| `health` | 3 | 3 | 100% | pronto para rotear |
| `payments` | 4 | 4 | 100% | pronto para rotear |
| `products` | 6 | 6 | 100% | pronto para rotear |
| `substitution-policy` | 2 | 2 | 100% | pronto para rotear |
| `coupons` | 6 | 5 | 83% | parcial |
| `loyalty` | 9 | 7 | 77% | parcial |
| `auth` | 13 | 10 | 76% | parcial |
| `stock` | 13 | 10 | 76% | parcial |
| `orders` | 13 | 8 | 61% | parcial |
| `substitutions` | 5 | 3 | 60% | parcial |
| `users` | 9 | 5 | 55% | parcial |
| `tenants` | 20 | 6 | 30% | parcial |
| `analytics` | 3 | 0 | 0% | não portado |
| `assembly-jobs` | 6 | 0 | 0% | não portado |
| `audit` | 1 | 0 | 0% | não portado |
| `availability` | 1 | 0 | 0% | não portado |
| `billing` | 6 | 0 | 0% | não portado |
| `campaigns` | 8 | 0 | 0% | não portado |
| `commercial` | 8 | 0 | 0% | não portado |
| `compositions` | 6 | 0 | 0% | não portado |
| `crm` | 3 | 0 | 0% | não portado |
| `customers` | 1 | 0 | 0% | não portado |
| `daily-pick` | 1 | 0 | 0% | não portado |
| `delivery-log` | 4 | 0 | 0% | não portado |
| `delivery-slots` | 1 | 0 | 0% | não portado |
| `finance` | 17 | 0 | 0% | não portado |
| `fiscal` | 4 | 0 | 0% | não portado |
| `fulfillments` | 3 | 0 | 0% | não portado |
| `i18n` | 5 | 0 | 0% | não portado |
| `integrations` | 2 | 0 | 0% | não portado |
| `inventory-items` | 6 | 0 | 0% | não portado |
| `inventory-source` | 3 | 0 | 0% | não portado |
| `metrics` | 4 | 0 | 0% | não portado |
| `notification-templates` | 3 | 0 | 0% | não portado |
| `operations` | 2 | 0 | 0% | não portado |
| `permissions` | 1 | 0 | 0% | não portado |
| `pickup-points` | 5 | 0 | 0% | não portado |
| `privacy` | 4 | 0 | 0% | não portado |
| `reviews` | 11 | 0 | 0% | não portado |
| `settings` | 3 | 0 | 0% | não portado |
| `showcase` | 5 | 0 | 0% | não portado |
| `storage` | 2 | 0 | 0% | não portado |
| `storefront-stats` | 1 | 0 | 0% | não portado |
| `whatsapp` | 3 | 0 | 0% | não portado |

## Operações do Nest ainda ausentes nos módulos parciais

<details><summary><code>coupons</code>: 1 faltando</summary>

- `GET /api/v1/coupons/{}/performance`

</details>

<details><summary><code>loyalty</code>: 2 faltando</summary>

- `POST /api/v1/loyalty/me/opt-in`
- `POST /api/v1/loyalty/me/opt-out`

</details>

<details><summary><code>auth</code>: 3 faltando</summary>

- `POST /api/v1/auth/admin/app-pairing`
- `POST /api/v1/auth/admin/app-pairing/exchange`
- `PATCH /api/v1/auth/admin/locale`

</details>

<details><summary><code>stock</code>: 3 faltando</summary>

- `GET /api/v1/stock/reconciliation-runs`
- `POST /api/v1/stock/reconciliation-runs`
- `GET /api/v1/stock/reconciliation-runs/{}`

</details>

<details><summary><code>orders</code>: 5 faltando</summary>

- `GET /api/v1/orders/export/csv`
- `POST /api/v1/orders/{}/bouquet-previews`
- `POST /api/v1/orders/{}/bouquet-previews/decision`
- `POST /api/v1/orders/{}/bouquet-previews/release`
- `GET /api/v1/orders/{}/realtime-ticket`

</details>

<details><summary><code>substitutions</code>: 2 faltando</summary>

- `GET /api/v1/substitutions`
- `POST /api/v1/substitutions`

</details>

<details><summary><code>users</code>: 4 faltando</summary>

- `GET /api/v1/users/me/permissions`
- `GET /api/v1/users/{}/permissions`
- `PUT /api/v1/users/{}/permissions`
- `DELETE /api/v1/users/{}/permissions/{}`

</details>

<details><summary><code>tenants</code>: 14 faltando</summary>

- `GET /api/v1/tenants`
- `GET /api/v1/tenants/current`
- `GET /api/v1/tenants/custom-domains`
- `PATCH /api/v1/tenants/me`
- `DELETE /api/v1/tenants/me/go-live`
- `GET /api/v1/tenants/me/go-live`
- `POST /api/v1/tenants/me/go-live`
- `POST /api/v1/tenants/me/onboarding/copy`
- `GET /api/v1/tenants/public-stats`
- `GET /api/v1/tenants/tls-allowed`
- `DELETE /api/v1/tenants/{}`
- `PATCH /api/v1/tenants/{}`
- `GET /api/v1/tenants/{}/users`
- `PATCH /api/v1/tenants/{}/users/{}/password`

</details>

## Só na API .NET

- `GET /api/v1/custom-quotes/{}`
- `GET /api/v1/orders/{}/substitutions`
- `GET /api/v1/platform/outbox`
- `POST /api/v1/platform/outbox/{}/retry`
- `GET /api/v1/settings/payments`
- `PUT /api/v1/settings/payments`
- `GET /api/v1/substitutions/{}`
