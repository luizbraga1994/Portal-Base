#!/usr/bin/env bash
# SMOKE TEST pós-deploy do Portal Base Web (Prompt 53, Fase 27). Valida os sinais mínimos de que o
# release subiu e está apto a receber tráfego — NÃO é E2E completo. Falha (exit != 0) trava a liberação
# de tráfego (Fase 28/30). Não imprime segredo. Uso: scripts/smoke-test.sh [BASE_URL]
#   BASE_URL padrão: http://localhost:8080
set -euo pipefail

base="${1:-http://localhost:8080}"
falhas=0

verificar() {
  local nome="$1" url="$2" esperado="${3:-200}"
  local codigo
  codigo="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "${url}" || echo 000)"
  if [[ "${codigo}" == "${esperado}" ]]; then
    echo "PASS  ${nome} (${codigo}) ${url}"
  else
    echo "FAIL  ${nome} (esperado ${esperado}, obtido ${codigo}) ${url}"
    falhas=$((falhas + 1))
  fi
}

echo "== Smoke test: ${base} =="
# 1. Liveness — processo vivo (Fase 19).
verificar "liveness"  "${base}/health/live"  200
# 2. Readiness — apto a atender (dependências críticas, ex.: catálogo).
verificar "readiness" "${base}/health/ready" 200
# 3. Dependency health — saúde agregada (tolerante; informativo).
verificar "dependencies" "${base}/health/dependencies" 200

# Confirma que o corpo do health é MÍNIMO (só status; sem vazar detalhe — §17 do P51).
corpo="$(curl -s --max-time 10 "${base}/health/live" || true)"
if echo "${corpo}" | grep -qiE 'password|connectionstring|server=|bearer'; then
  echo "FAIL  health expõe detalhe sensível"
  falhas=$((falhas + 1))
else
  echo "PASS  health mínimo (sem detalhe sensível)"
fi

if [[ "${falhas}" -gt 0 ]]; then
  echo "== SMOKE FAIL (${falhas} falha(s)) — NÃO liberar tráfego =="
  exit 1
fi
echo "== SMOKE PASS =="
