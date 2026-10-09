using PortalBase.Application.Auditoria;
using PortalBase.Web.Seguranca;
using PortalBase.Web.Ui.Autorizacao;

namespace PortalBase.Web.Ui.Dados;

/// <summary>
/// Fonte de dados da tela de Auditoria. Ligada ao READ-MODEL REAL do backend
/// (<see cref="ConsultaAuditoriaAutorizada"/>, autorizado + paginado + auto-auditado). O tenant é
/// o resolvido no servidor (<see cref="SelecaoTenant.TenantAtual"/>), nunca vindo do cliente. Sem
/// tenant → estado vazio (fail-closed); erro/negação → estado seguro (sem vazar detalhe — §49). Em
/// demonstração usa dados simulados e rotulados.
/// </summary>
public interface IDadosAuditoria
{
    Task<ResultadoAuditoriaVm> ConsultarAsync(FiltroAuditoriaVm filtro, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DadosAuditoria : IDadosAuditoria
{
    private readonly IAutorizacaoVisual _visual;
    private readonly ConsultaAuditoriaAutorizada _consulta;
    private readonly SelecaoTenant _selecao;

    public DadosAuditoria(IAutorizacaoVisual visual, ConsultaAuditoriaAutorizada consulta, SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(selecao);
        _visual = visual;
        _consulta = consulta;
        _selecao = selecao;
    }

    public async Task<ResultadoAuditoriaVm> ConsultarAsync(FiltroAuditoriaVm filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);

        if (_visual.Demonstracao)
        {
            return AuditoriaDemonstracao.Consultar(filtro);
        }

        var tenant = _selecao.TenantAtual;
        if (tenant is null)
        {
            return Vazio(filtro, EstadoConsultaAuditoria.SemTenant);
        }

        try
        {
            // O Tenant do filtro é ignorado/forçado pelo servidor ao tenant autorizado.
            var filtroApp = new FiltroAuditoria
            {
                Tenant = string.Empty,
                Desde = filtro.Desde,
                Ate = filtro.Ate,
                Ator = Limpar(filtro.Ator),
                Acao = Limpar(filtro.Acao),
                Resultado = filtro.Resultado is "Todos" ? null : Limpar(filtro.Resultado),
                TipoRecurso = Limpar(filtro.TipoRecurso),
                CorrelationId = Limpar(filtro.CorrelationId),
                Pagina = filtro.Pagina,
                TamanhoPagina = filtro.TamanhoPagina,
            };

            var pagina = await _consulta.ConsultarAsync(tenant.Value, filtroApp, cancellationToken).ConfigureAwait(false);
            var itens = pagina.Itens.Select(Mapear).ToList();
            return new ResultadoAuditoriaVm(itens, pagina.Pagina, pagina.TamanhoPagina, pagina.Total, EstadoConsultaAuditoria.Ok);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            // Fail-closed e sem vazar detalhe interno (regra 16 / §49). Cobre negação de autorização,
            // ausência de contexto de tenant e indisponibilidade do catálogo.
            return Vazio(filtro, EstadoConsultaAuditoria.Erro);
        }
    }

    private static ResultadoAuditoriaVm Vazio(FiltroAuditoriaVm filtro, EstadoConsultaAuditoria estado) =>
        new([], filtro.Pagina < 1 ? 1 : filtro.Pagina, filtro.TamanhoPagina, 0, estado);

    private static string? Limpar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static RegistroAuditoriaVm Mapear(RegistroAuditoriaDto d) => new(
        d.Id, d.PrincipalId, d.TipoAtor, d.Acao, Recurso(d), d.Resultado, d.Motivo,
        d.CorrelationId, d.Protocolo, d.Competencia, d.OcorridoEmUtc, d.NomeAtor, d.LoginAtor);

    private static string? Recurso(RegistroAuditoriaDto d)
    {
        if (string.IsNullOrEmpty(d.TipoRecurso))
        {
            return null;
        }

        return string.IsNullOrEmpty(d.RecursoId) ? d.TipoRecurso : d.TipoRecurso + " · " + d.RecursoId;
    }
}
