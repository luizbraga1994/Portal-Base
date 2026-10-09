#!/usr/bin/env bash
# Build REPRODUZÍVEL das imagens do Portal Base (Prompt 53, Fase 6/7).
# Injeta versão/commit/timestamp como --build-arg → rótulos OCI + PORTALBASE_BUILD_* no runtime.
# NÃO embute segredo. Uso: scripts/build-image.sh [web|worker|all] [VERSION]
set -euo pipefail

alvo="${1:-all}"
versao="${2:-2.0.0}"
sha="$(git rev-parse --short HEAD 2>/dev/null || echo desconhecido)"
timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

build() {
  local dockerfile="$1" nome="$2"
  echo ">> build ${nome}: versao=${versao} commit=${sha} build=${timestamp}"
  docker build \
    -f "${dockerfile}" \
    --build-arg "BUILD_VERSION=${versao}" \
    --build-arg "BUILD_SHA=${sha}" \
    --build-arg "BUILD_TIMESTAMP=${timestamp}" \
    -t "portalbase-${nome}:${versao}" \
    -t "portalbase-${nome}:${sha}" \
    .
}

case "${alvo}" in
  web)    build Dockerfile.Web web ;;
  worker) build Dockerfile.Worker worker ;;
  all)    build Dockerfile.Web web; build Dockerfile.Worker worker ;;
  *) echo "uso: $0 [web|worker|all] [VERSION]" >&2; exit 2 ;;
esac

echo ">> concluído. Rótulos OCI (org.opencontainers.image.*) e PORTALBASE_BUILD_* aplicados."
