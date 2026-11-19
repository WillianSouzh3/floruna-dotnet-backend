// Teste de carga "Dia das Mães" (RNF-01, RNF-03).
//
// Dois públicos ao mesmo tempo, como no pico real:
//   - browse:   vitrine (listagem e detalhe), a maior parte do tráfego;
//   - checkout: compras, 70% delas no MESMO buquê disputado, de estoque limitado.
//
// O que se verifica:
//   - latência: catálogo p95 < 150 ms, checkout p95 < 800 ms (RNF-01);
//   - erro: < 1% (201 e 409 "sem estoque" são respostas ESPERADAS, não falhas);
//   - oversell ZERO: no fim, o buquê disputado nunca tem mais reservado/vendido do que o estoque inicial (RNF-03).
//
// Uso (API local com o rate limit de checkout aumentado para o teste, ver load/README.md):
//   docker run --rm -i --network host -e BASE_URL=http://localhost:4000 -v "$PWD/load:/load" grafana/k6 run /load/mothers-day.js
import http from 'k6/http';
import { check, fail } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:4000';
const HOT_STOCK = Number(__ENV.HOT_STOCK || 300);
const PEAK_BROWSE = Number(__ENV.PEAK_BROWSE || 150); // requisições/s na vitrine
const PEAK_CHECKOUT = Number(__ENV.PEAK_CHECKOUT || 30); // checkouts/s
const HOLD = __ENV.HOLD || '60s';

const ordersCreated = new Counter('floruna_orders_created');
const soldOut = new Counter('floruna_checkout_sold_out');

export const options = {
  scenarios: {
    browse: {
      executor: 'ramping-arrival-rate', exec: 'browse', timeUnit: '1s', startRate: 10,
      preAllocatedVUs: 50, maxVUs: 300,
      stages: [{ target: PEAK_BROWSE, duration: '20s' }, { target: PEAK_BROWSE, duration: HOLD }, { target: 0, duration: '10s' }],
    },
    checkout: {
      executor: 'ramping-arrival-rate', exec: 'checkout', timeUnit: '1s', startRate: 1,
      preAllocatedVUs: 50, maxVUs: 300,
      stages: [{ target: PEAK_CHECKOUT, duration: '20s' }, { target: PEAK_CHECKOUT, duration: HOLD }, { target: 0, duration: '10s' }],
    },
  },
  thresholds: {
    'http_req_duration{scenario:browse}': ['p(95)<150'],
    'http_req_duration{scenario:checkout}': ['p(95)<800'],
    'http_req_failed{scenario:browse}': ['rate<0.01'],
    'http_req_failed{scenario:checkout}': ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

// 409 = "estoque insuficiente": é o sistema dizendo NÃO corretamente, não uma falha.
http.setResponseCallback(http.expectedStatuses(200, 201, 409));

function json(res) {
  try { return res.json(); } catch { return null; }
}

function post(path, body, headers) {
  return http.post(`${BASE}${path}`, JSON.stringify(body), { headers: Object.assign({ 'Content-Type': 'application/json' }, headers) });
}

export function setup() {
  const slug = `carga-${Date.now().toString(36)}`;
  const email = `dona@${slug}.com.br`;
  const password = 'Senha-Forte-123!';
  const host = { 'x-tenant-host': `${slug}.localhost` };

  const signup = post('/api/v1/tenants/signup', { storeName: 'Loja Carga', slug, adminName: 'Dona', email, password });
  if (signup.status !== 201 && signup.status !== 200) fail(`signup ${signup.status}: ${signup.body}`);
  const login = post('/api/v1/auth/admin/login', { email, password }, host);
  const token = json(login)?.data?.accessToken || fail(`login ${login.status}: ${login.body}`);
  const admin = Object.assign({ Authorization: `Bearer ${token}` }, host);

  const product = (name, stock) => {
    const res = post('/api/v1/products', {
      name, shortDescription: 'Buquê para o Dia das Mães', description: 'Flores frescas, montagem no dia, cartão incluso.',
      category: 'buques', price: 15990, images: ['https://cdn.test/buque.webp'], stock, isActive: true, isFeatured: true,
    }, admin);
    return json(res)?.data?.product || fail(`produto ${res.status}: ${res.body}`);
  };

  const hot = product('Buquê Mãe Rainha', HOT_STOCK);
  const catalog = [];
  for (let i = 0; i < 12; i++) catalog.push(product(`Arranjo ${i + 1}`, 1000000));
  return { host, admin, hot: hot.id, catalog: catalog.map((p) => ({ id: p.id, slug: p.slug })) };
}

export function browse(data) {
  const pick = data.catalog[Math.floor(Math.random() * data.catalog.length)];
  const res = Math.random() < 0.6
    ? http.get(`${BASE}/api/v1/products?limit=20`, { headers: data.host, tags: { name: 'GET /products' } })
    : http.get(`${BASE}/api/v1/products/${pick.slug}`, { headers: data.host, tags: { name: 'GET /products/{slug}' } });
  check(res, { 'vitrine 200': (r) => r.status === 200 });
}

export function checkout(data) {
  const productId = Math.random() < 0.7 ? data.hot : data.catalog[Math.floor(Math.random() * data.catalog.length)].id;
  const res = post('/api/v1/orders', {
    idempotencyKey: `${__VU}-${__ITER}-${Date.now()}`,
    customerName: 'Cliente Carga', customerPhone: '(11) 99999-0000', customerEmail: 'cliente@carga.com.br',
    items: [{ productId, quantity: 1 }], deliveryMethod: 'retirada',
  }, data.host);
  if (res.status === 201) ordersCreated.add(1);
  if (res.status === 409) soldOut.add(1);
  check(res, { 'checkout 201 ou 409': (r) => r.status === 201 || r.status === 409 });
}

export function teardown(data) {
  const res = http.get(`${BASE}/api/v1/products/manage?limit=100`, { headers: data.admin });
  const hot = (json(res)?.data?.products || []).find((p) => p.id === data.hot);
  if (!hot) fail('buquê disputado não encontrado no teardown');
  const a = hot.availability;
  console.log(`buquê disputado: estoque inicial ${HOT_STOCK}, onHand ${a.onHand}, reservado ${a.reserved}, disponível ${a.available}`);
  // Sem oversell: nunca reservado além do estoque, nunca disponível negativo.
  if (a.reserved > HOT_STOCK || a.available < 0) fail(`OVERSELL: reservado ${a.reserved} de ${HOT_STOCK}, disponível ${a.available}`);
}
