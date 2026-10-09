// Prompt 77 — PERFIS de DATASET SINTÉTICO para teste de carga (Fase 6).
//
// Estes números são PARÂMETROS DE TESTE (volumetria a semear num ambiente DEDICADO), NÃO capacidade
// declarada do produto. Refletem a estrutura real do domínio (tenants → usuários → arquivos →
// registros). NUNCA usar dados de produção, PII real, credenciais
// reais ou arquivos sensíveis: o seed do ambiente de carga cria dados sintéticos próprios
// (ver perf/README.md e o padrão de SemeadorRoundTripE2E do P76).

export const DATASETS = {
  // SMALL — pipeline/dev: um contribuinte, poucos dados. Valida o harness sem exigir muita infra.
  small: {
    tenants: 1,
    usuariosPorTenant: 2,
    arquivosPorTenant: 15,
    registrosPorArquivo: 20,
  },
  // MEDIUM — carga esperada: alguns contribuintes concorrentes, volume operacional.
  medium: {
    tenants: 3,
    usuariosPorTenant: 5,
    arquivosPorTenant: 240,
    registrosPorArquivo: 50,
  },
  // LARGE — estresse/soak: muitos contribuintes e muitos arquivos.
  large: {
    tenants: 10,
    usuariosPorTenant: 10,
    arquivosPorTenant: 3600,
    registrosPorArquivo: 100,
  },
};

export function perfilDataset(nome) {
  return DATASETS[(nome || 'small').toLowerCase()] || DATASETS.small;
}
