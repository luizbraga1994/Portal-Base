// Prompt 56/77 — Harness de carga k6 do Portal Base.
//
// FERRAMENTA PRINCIPAL de carga HTTP (Fase 34): k6. O Playwright cobre a experiência de Browser (poucos
// usuários; ver PerformanceSmokeE2ETests). A corretude do Worker é dos testes de integração.
//
// Este script NÃO roda no ambiente de build (sem MySQL/Web/Worker/k6/infra de carga — BLOQUEIO EXTERNO
// REAL). É o artefato reproduzível para um ambiente DEDICADO/descartável. Não declara capacidade: os
// números só existem quando executado de fato contra infra real.
//
// SEGURANÇA (Fase 5/22): NADA hardcoded — BASE_URL, credenciais, tenant e segredo de MFA vêm do
// ambiente. NUNCA aponte para produção, API externa real ou banco de cliente. As credenciais são de um
// USUÁRIO DE TESTE semeado no ambiente de carga (nunca reais). Sem credenciais, o script exercita apenas
// endpoints ANÔNIMOS (liveness + render de login) — sem bypass de autenticação.
//
// Uso:
//   BASE_URL=http://127.0.0.1:8080 k6 run perf/load-test.js
//   BASE_URL=http://127.0.0.1:8080 PERFIL=stress k6 run perf/load-test.js
//   # autenticado (ambiente dedicado com usuário de teste semeado):
//   BASE_URL=... LOAD_TEST_USER=u LOAD_TEST_PASSWORD=p LOAD_TEST_TENANT=<guid> \
//     LOAD_TEST_MFA_SECRET=<base32> PERFIL=load k6 run perf/load-test.js
//   # multi-tenant (JSON de usuários de teste, um por tenant):
//   BASE_URL=... LOAD_TEST_USERS_JSON='[{"login":"a","senha":"..","tenant":"..","mfaSegredo":".."}]' k6 run ...

import http from 'k6/http';
import { check, group, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { gerarTotp } from './totp.js';

// ------------------------------------------------------------------ configuração externalizada (Fase 22)
const BASE = (__ENV.BASE_URL || 'http://127.0.0.1:8080').replace(/\/+$/, '');
const PERFIL = (__ENV.PERFIL || 'smoke').toLowerCase();
const VUS_OVERRIDE = parseInt(__ENV.VUS || '', 10);
const DURATION_OVERRIDE = __ENV.DURATION || '';
const PENSA_SEGUNDOS = parseFloat(__ENV.THINK_TIME || '1');

// Usuários de teste (nunca reais). Prioridade: LOAD_TEST_USERS_JSON (multi-tenant) > variáveis simples.
const USUARIOS = new SharedArray('usuarios', function () {
  if (__ENV.LOAD_TEST_USERS_JSON) {
    try {
      const lista = JSON.parse(__ENV.LOAD_TEST_USERS_JSON);
      if (Array.isArray(lista) && lista.length > 0) {
        return lista;
      }
    } catch (e) {
      throw new Error('LOAD_TEST_USERS_JSON invalido (esperado um array JSON de usuarios de teste).');
    }
  }
  if (__ENV.LOAD_TEST_USER && __ENV.LOAD_TEST_PASSWORD) {
    return [{
      login: __ENV.LOAD_TEST_USER,
      senha: __ENV.LOAD_TEST_PASSWORD,
      tenant: __ENV.LOAD_TEST_TENANT || '',
      mfaSegredo: __ENV.LOAD_TEST_MFA_SECRET || '',
    }];
  }
  return []; // sem credenciais → só anônimo (sem bypass)
});

const AUTENTICADO = USUARIOS.length > 0;

// ------------------------------------------------------------------ cenários por OBJETIVO (Fase 3)
// Cada perfil tem um objetivo específico; não há "carga indiscriminada". Ajuste os alvos à capacidade
// do ambiente dedicado — estes são pontos de partida, não capacidade do produto.
const PERFIS = {
  // SMOKE: o serviço responde sob carga pequena (valida o pipeline).
  smoke:    [{ duration: '20s', target: 5 }, { duration: '20s', target: 5 }, { duration: '10s', target: 0 }],
  // BASELINE: linha de base leve e estável para comparação de regressão.
  baseline: [{ duration: '30s', target: 10 }, { duration: '2m', target: 10 }, { duration: '30s', target: 0 }],
  // LOAD: carga operacional esperada.
  load:     [{ duration: '1m', target: 20 }, { duration: '3m', target: 20 }, { duration: '1m', target: 0 }],
  // STRESS: acima do operacional, subindo até degradação observável.
  stress:   [{ duration: '1m', target: 50 }, { duration: '2m', target: 100 }, { duration: '2m', target: 200 }, { duration: '1m', target: 0 }],
  // SOAK: carga sustentada para detectar degradação progressiva / vazamento / crescimento de backlog.
  soak:     [{ duration: '2m', target: 20 }, { duration: '56m', target: 20 }, { duration: '2m', target: 0 }],
  // CONCORRÊNCIA (spike): rajada abrupta para expor contenção/lock/fila.
  spike:    [{ duration: '15s', target: 200 }, { duration: '45s', target: 200 }, { duration: '15s', target: 0 }],
};

// Override direto por VUS+DURATION (executor de VUs constantes) tem precedência sobre o perfil de estágios.
const usaConstante = Number.isFinite(VUS_OVERRIDE) && VUS_OVERRIDE > 0 && DURATION_OVERRIDE !== '';

const cenario = usaConstante
  ? { executor: 'constant-vus', vus: VUS_OVERRIDE, duration: DURATION_OVERRIDE }
  : { executor: 'ramping-vus', startVUs: 0, stages: (PERFIS[PERFIL] || PERFIS.smoke), gracefulRampDown: '10s' };

export const options = {
  scenarios: { principal: Object.assign({ tags: { perfil: PERFIL } }, cenario) },
  // THRESHOLDS = guardrails de SMOKE/REGRESSÃO (Fase 11) — NÃO são SLA/capacidade do produto (o repo não
  // define SLA oficial). Servem para detectar regressão grosseira do próprio ambiente de teste.
  thresholds: {
    http_req_failed: ['rate<0.01'],                    // regression guardrail
    http_req_duration: ['p(95)<800'],                  // regression guardrail (não SLA)
    'http_req_duration{rota:health}': ['p(95)<300'],   // regression guardrail
    checks: ['rate>0.99'],                             // correctness guardrail
  },
};

// ------------------------------------------------------------------ helpers (sem vazamento — Fase 10)
const REGEX_SEGREDO = /Server=|MySqlException|-----BEGIN|Bearer eyJ|ConnectionString/i;
const REGEX_ANTIFORGERY = /name="__RequestVerificationToken"[^>]*value="([^"]+)"/i;

function semVazamento(r) {
  return !REGEX_SEGREDO.test(r.body || '');
}

function tokenAntiforgery(corpo) {
  const m = REGEX_ANTIFORGERY.exec(corpo || '');
  return m ? m[1] : '';
}

// Login REAL pelas fronteiras da aplicação (antiforgery + senha + STEP-UP de MFA + seleção de tenant).
// Sem bypass: mesma sequência que um usuário faria. A cookie jar do k6 mantém a sessão por VU.
function autenticar(usuario) {
  const entrar = http.get(`${BASE}/conta/entrar`, { tags: { rota: 'login-get' } });
  const t1 = tokenAntiforgery(entrar.body);
  const login = http.post(`${BASE}/conta/login`, {
    __RequestVerificationToken: t1,
    login: usuario.login,
    senha: usuario.senha,
    returnUrl: '/',
  }, { tags: { rota: 'login-post' } });
  check(login, { 'login aceito (2xx/3xx)': (r) => r.status < 400 });

  // STEP-UP de MFA (o app exige segundo fator). Requer o segredo do usuário de teste semeado.
  const verificar = http.get(`${BASE}/conta/verificar`, { tags: { rota: 'mfa-get' } });
  if ((verificar.url || '').indexOf('/conta/verificar') >= 0 && usuario.mfaSegredo) {
    const t2 = tokenAntiforgery(verificar.body);
    const codigo = gerarTotp(usuario.mfaSegredo, Math.floor(Date.now() / 1000));
    const mfa = http.post(`${BASE}/conta/mfa`, {
      __RequestVerificationToken: t2,
      codigo: codigo,
    }, { tags: { rota: 'mfa-post' } });
    check(mfa, { 'mfa aceito (2xx/3xx)': (r) => r.status < 400 });
  }

  // Seleção de contribuinte (claims de tenant no cookie).
  if (usuario.tenant) {
    const selecionar = http.get(`${BASE}/conta/selecionar`, { tags: { rota: 'tenant-get' } });
    const t3 = tokenAntiforgery(selecionar.body);
    http.post(`${BASE}/conta/contribuinte`, {
      __RequestVerificationToken: t3,
      tenantId: usuario.tenant,
    }, { tags: { rota: 'tenant-post' } });
  }
}

// Estado por VU (cada VU é uma instância JS isolada no k6): autentica UMA vez e reusa a sessão (cookie
// jar do VU) nas iterações seguintes — mede a leitura DB-backed, não o custo repetido de login.
let sessaoIniciada = false;

// ------------------------------------------------------------------ setup: registra a intenção
export function setup() {
  if (AUTENTICADO) {
    console.log(`Harness AUTENTICADO: ${USUARIOS.length} usuario(s) de teste; perfil=${PERFIL}; alvo=${BASE}`);
  } else {
    console.log(`Harness ANONIMO (sem credenciais): so liveness + render de login; perfil=${PERFIL}; alvo=${BASE}`);
  }
}

// ------------------------------------------------------------------ iteração do VU
export default function () {
  group('saude', function () {
    const liveness = http.get(`${BASE}/health/live`, { tags: { rota: 'health' } });
    check(liveness, { 'health 200': (r) => r.status === 200, 'health sem vazamento': semVazamento });
  });

  group('render-anonimo', function () {
    const login = http.get(`${BASE}/conta/entrar`, { tags: { rota: 'login' } });
    check(login, { 'login 200': (r) => r.status === 200, 'login sem vazamento': semVazamento });
  });

  if (AUTENTICADO) {
    // Multi-tenant (Fase 18): VUs distribuídos round-robin entre os usuários/tenants de teste.
    const usuario = USUARIOS[(__VU - 1) % USUARIOS.length];
    group('autenticado-leitura', function () {
      if (!sessaoIniciada) {
        autenticar(usuario);
        sessaoIniciada = true; // sessão reusada nas próximas iterações do mesmo VU
      }
      // Leitura DB-backed (read-model paginado) — a fila de operações do tenant. O filtro é do servidor.
      const backlog = http.get(`${BASE}/operacoes/backlog`, { tags: { rota: 'backlog' } });
      check(backlog, {
        'backlog acessivel (2xx/3xx)': (r) => r.status < 400,
        'backlog sem vazamento': semVazamento,
      });
    });
  }

  sleep(PENSA_SEGUNDOS);
}

// ------------------------------------------------------------------ exportação de resultados (Fase 2)
export function handleSummary(data) {
  return {
    stdout: textoResumo(data),
    'artifacts/k6-summary.json': JSON.stringify(data, null, 2),
  };
}

function textoResumo(data) {
  const m = data.metrics || {};
  const dur = (m.http_req_duration && m.http_req_duration.values) || {};
  const fail = (m.http_req_failed && m.http_req_failed.values) || {};
  const linhas = [
    '',
    `== k6 Portal Base — perfil=${PERFIL} autenticado=${AUTENTICADO} ==`,
    `req_duration  p50=${fmt(dur['p(50)'])}ms p95=${fmt(dur['p(95)'])}ms p99=${fmt(dur['p(99)'])}ms max=${fmt(dur.max)}ms`,
    `req_failed    rate=${fmt((fail.rate || 0) * 100)}%`,
    `(numeros MEDIDOS deste ambiente/execucao — nao sao capacidade declarada do produto)`,
    '',
  ];
  return linhas.join('\n');
}

function fmt(v) {
  return typeof v === 'number' ? Math.round(v * 10) / 10 : 'n/a';
}
