using PortalBase.Application.Auditoria;
using PortalBase.Application.Consultas;

namespace PortalBase.Web.Ui.Dados;

/// <summary>
/// Gerador de dados de auditoria APENAS PARA DEMONSTRAÇÃO (rotulado na UI). Ações reais do catálogo
/// de auditoria; nenhum segredo/payload. Aplica filtro e paginação da mesma forma que o backend
/// (mais recentes primeiro), para exercitar filtros/paginação sem banco.
/// </summary>
public static class AuditoriaDemonstracao
{
    public static ResultadoAuditoriaVm Consultar(FiltroAuditoriaVm filtro)
    {
        ArgumentNullException.ThrowIfNull(filtro);

        // Mesma regra de período do backend (Prompt 81): Desde > Até é rejeitado, nunca "corrigido".
        if (!PeriodoConsulta.EhValido(filtro.Desde, filtro.Ate))
        {
            return new ResultadoAuditoriaVm([], filtro.Pagina < 1 ? 1 : filtro.Pagina, filtro.TamanhoPagina, 0, EstadoConsultaAuditoria.Erro);
        }

        IEnumerable<RegistroAuditoriaVm> itens = Base();

        if (filtro.Desde is { } desde)
        {
            itens = itens.Where(r => r.OcorridoEmUtc >= desde);
        }

        if (filtro.Ate is { } ate)
        {
            itens = itens.Where(r => r.OcorridoEmUtc <= ate);
        }

        if (!string.IsNullOrWhiteSpace(filtro.Ator))
        {
            itens = itens.Where(r => r.Ator.Contains(filtro.Ator, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filtro.Acao))
        {
            itens = itens.Where(r => r.Acao.Contains(filtro.Acao, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filtro.Resultado) &&
            !string.Equals(filtro.Resultado, "Todos", StringComparison.OrdinalIgnoreCase))
        {
            itens = itens.Where(r => string.Equals(r.Resultado, filtro.Resultado, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filtro.TipoRecurso))
        {
            itens = itens.Where(r => (r.Recurso ?? string.Empty).Contains(filtro.TipoRecurso, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filtro.CorrelationId))
        {
            itens = itens.Where(r => (r.CorrelationId ?? string.Empty).Contains(filtro.CorrelationId, StringComparison.OrdinalIgnoreCase));
        }

        var todos = itens.OrderByDescending(r => r.OcorridoEmUtc).ToList();
        var tamanho = filtro.TamanhoPagina < 1 ? 25 : filtro.TamanhoPagina;
        var pagina = filtro.Pagina < 1 ? 1 : filtro.Pagina;
        var recorte = todos.Skip((pagina - 1) * tamanho).Take(tamanho).ToList();

        return new ResultadoAuditoriaVm(recorte, pagina, tamanho, todos.Count, EstadoConsultaAuditoria.Demonstracao);
    }

    private static IReadOnlyList<RegistroAuditoriaVm> Base()
    {
        var t = new DateTimeOffset(2025, 8, 20, 14, 0, 0, TimeSpan.Zero);
        long id = 1000;
        return
        [
            Reg(ref id, t.AddMinutes(-2), "ana.paula", TipoAtor.Usuario, "UserLoggedIn", "Sessao", "Permitido", null, "a1f9"),
            Reg(ref id, t.AddMinutes(-1), "ana.paula", TipoAtor.Usuario, "MfaValidated", "Sessao", "Permitido", null, "a1f9"),
            Reg(ref id, t, "ana.paula", TipoAtor.Usuario, "TenantContextSwitched", "Tenant", "Permitido", null, "a1f9"),
            Reg(ref id, t.AddMinutes(5), "ana.paula", TipoAtor.Usuario, "UploadAccepted", "Arquivo · 2025-08 relatorio.xlsx", "Permitido", null, "b3c2"),
            Reg(ref id, t.AddMinutes(9), "sistema", TipoAtor.Sistema, "UploadStored", "Arquivo · 2025-08 relatorio.xlsx", "Permitido", null, "b3c2"),
            Reg(ref id, t.AddMinutes(14), "carlos.dev", TipoAtor.Usuario, "RoleAssigned", "Usuario · ana.paula", "Permitido", null, "d7e1"),
            Reg(ref id, t.AddMinutes(20), "carlos.dev", TipoAtor.Usuario, "UserCreated", "Usuario · joao.silva", "Permitido", null, "e2a8"),
            Reg(ref id, t.AddMinutes(26), "carlos.dev", TipoAtor.Usuario, "MarcaTenantAtualizada", "Marca · logo do contribuinte", "Permitido", null, "f4b0"),
            RegProt(ref id, t.AddMinutes(31), "trab.integracao", TipoAtor.JobBackground, "IntegracaoHabilitada", "Integracao · API externa 2025-08", "Permitido", "f4b0", "PROT-2025-08-000123"),
            Reg(ref id, t.AddMinutes(48), "trab.backup", TipoAtor.JobBackground, "BackupStarted", "Backup · catálogo", "Permitido", null, "a9c4"),
            Reg(ref id, t.AddMinutes(55), "trab.backup", TipoAtor.JobBackground, "BackupCompleted", "Backup · catálogo", "Permitido", null, "a9c4"),
            RegProt(ref id, t.AddMinutes(63), "sistema", TipoAtor.Sistema, "UploadDuplicated", "Arquivo · 2025-08 relatorio.xlsx", "Permitido", "b3c2", "PROT-2025-08-000199"),
            RegNeg(ref id, t.AddMinutes(70), "estag.joao", TipoAtor.Usuario, "TenantAccessDenied", "Tenant", "SemVinculo"),
            Reg(ref id, t.AddMinutes(75), "ana.paula", TipoAtor.Usuario, "AuditQueried", "Auditoria", "Permitido", null, "c8d3"),
        ];
    }

    private static RegistroAuditoriaVm Reg(ref long id, DateTimeOffset quando, string ator, TipoAtor tipo, string acao, string recurso, string resultado, string? motivo, string? corr) =>
        new(++id, ator, tipo, acao, recurso, resultado, motivo, corr, null, ExtrairCompetencia(recurso), quando);

    private static RegistroAuditoriaVm RegProt(ref long id, DateTimeOffset quando, string ator, TipoAtor tipo, string acao, string recurso, string resultado, string? corr, string protocolo) =>
        new(++id, ator, tipo, acao, recurso, resultado, null, corr, protocolo, ExtrairCompetencia(recurso), quando);

    private static RegistroAuditoriaVm RegNeg(ref long id, DateTimeOffset quando, string ator, TipoAtor tipo, string acao, string recurso, string motivo) =>
        new(++id, ator, tipo, acao, recurso, "Negado", motivo, null, null, null, quando);

    private static string? ExtrairCompetencia(string recurso) =>
        recurso.Contains("2025-08", StringComparison.Ordinal) ? "2025-08" : null;
}
