using PortalBase.Application.Consultas;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Incidentes;
using PortalBase.Application.Observabilidade;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// FONTES DE DADOS do Frontend Administração e Operações (Prompt 40). Embrulham os read-models autorizados
// de PLATAFORMA (Incidentes §27, Operações §28) — cross-tenant, exigem seguranca.incidentes.gerir no
// servidor. Só leitura; nenhum command. Mapeiam resultado/exceções para estado de UI seguro (fail-closed,
// sem vazar detalhe). NUNCA convertem ausência de telemetria em "saudável" (§8).
// =====================================================================================================

/// <summary>Formatação/badges das telas de Administração e Operações (cor + texto — nunca só cor).</summary>
public static class AdminOpsUi
{
    // ---- Incidentes ----
    public static string BadgeSeveridade(SeveridadeIncidente s) => s switch
    {
        SeveridadeIncidente.Critico or SeveridadeIncidente.Alto => "bad",
        SeveridadeIncidente.Medio => "warn",
        SeveridadeIncidente.Baixo => "info",
        _ => "neutral",
    };

    public static string BadgeStatusIncidente(StatusIncidente s) => s switch
    {
        StatusIncidente.Encerrado => "ok",
        StatusIncidente.Contido => "primary",
        StatusIncidente.EmAnalise => "warn",
        _ => "bad",
    };

    public static string RotuloStatusIncidente(StatusIncidente s) => s switch
    {
        StatusIncidente.EmAnalise => "Em análise",
        _ => s.ToString(),
    };

    public static string RotuloTipoIncidente(TipoIncidente t) => t switch
    {
        TipoIncidente.Tecnico => "Técnico",
        TipoIncidente.ComDadosPessoais => "Com dados pessoais",
        _ => t.ToString(),
    };

    public static string BadgeRisco(ClassificacaoRiscoTitular c) => c switch
    {
        ClassificacaoRiscoTitular.ComRiscoOuDanoRelevante => "bad",
        ClassificacaoRiscoTitular.SemRiscoRelevante => "ok",
        _ => "warn",
    };

    public static string RotuloRisco(ClassificacaoRiscoTitular c) => c switch
    {
        ClassificacaoRiscoTitular.NaoAvaliado => "Não avaliado",
        ClassificacaoRiscoTitular.SemRiscoRelevante => "Sem risco relevante",
        ClassificacaoRiscoTitular.ComRiscoOuDanoRelevante => "Com risco/dano relevante",
        _ => c.ToString(),
    };

    // ---- Operações ----
    public static (string Rotulo, string Badge) EstadoWorker(bool ativo, bool estagnado)
    {
        if (estagnado)
        {
            return ("Estagnado", "bad");
        }

        return ativo ? ("Ativo", "ok") : ("Inativo", "neutral");
    }

    public static (string Rotulo, string Badge) EstadoIntegracao(bool degradada) =>
        degradada ? ("Degradada", "bad") : ("Saudável", "ok");
}

// ---- Incidentes -------------------------------------------------------------------------------------

public interface IDadosIncidentes
{
    Task<ResultadoListaVm<RegistroIncidenteVm>> ConsultarAsync(FiltroIncidentesVm filtro, CancellationToken cancellationToken = default);

    Task<(RegistroIncidenteVm? Item, EstadoConsultaUi Estado)> ObterAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class DadosIncidentes : IDadosIncidentes
{
    private readonly ConsultaIncidentesAutorizada _consulta;

    public DadosIncidentes(ConsultaIncidentesAutorizada consulta)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        _consulta = consulta;
    }

    public async Task<ResultadoListaVm<RegistroIncidenteVm>> ConsultarAsync(FiltroIncidentesVm filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        try
        {
            var app = new FiltroIncidentes
            {
                Desde = filtro.Desde,
                Ate = filtro.Ate,
                Tipo = ParseEnum<TipoIncidente>(filtro.Tipo),
                Severidade = ParseEnum<SeveridadeIncidente>(filtro.Severidade),
                Status = ParseEnum<StatusIncidente>(filtro.Status),
                TenantAfetadoId = filtro.TenantAfetadoId,
                Codigo = Limpar(filtro.Codigo),
                Ordenacao = ParseEnum<CampoOrdenacaoIncidente>(filtro.Ordenacao) ?? CampoOrdenacaoIncidente.Detectado,
                Direcao = filtro.Descendente ? DirecaoOrdenacao.Descendente : DirecaoOrdenacao.Ascendente,
                Pagina = filtro.Pagina,
                TamanhoPagina = filtro.TamanhoPagina,
            };
            var p = await _consulta.ConsultarAsync(app, cancellationToken).ConfigureAwait(false);
            var itens = p.Itens.Select(Mapear).ToList();
            return new ResultadoListaVm<RegistroIncidenteVm>(itens, p.Pagina, p.TamanhoPagina, p.TotalItens, EstadoConsultaUi.Ok);
        }
        catch (NaoAutenticadoException)
        {
            return ResultadoListaVm.Vazio<RegistroIncidenteVm>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.SemPermissao);
        }
        catch (AutorizacaoNegadaException)
        {
            return ResultadoListaVm.Vazio<RegistroIncidenteVm>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return ResultadoListaVm.Vazio<RegistroIncidenteVm>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.Erro);
        }
    }

    // Prompt 73: consulta DIRETA por Id (read-model de primeira classe) — sem "buscar página + FirstOrDefault".
    // Incidentes são governança de PLATAFORMA (cross-tenant): autorização seguranca.incidentes.gerir no read-model,
    // sem exigência de tenant. Espelha o tratamento de exceções da listagem (fail-closed, sem vazar detalhe).
    public async Task<(RegistroIncidenteVm? Item, EstadoConsultaUi Estado)> ObterAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var item = await _consulta.ObterAsync(id, cancellationToken).ConfigureAwait(false);
            return item is null ? (null, EstadoConsultaUi.Ok) : (Mapear(item), EstadoConsultaUi.Ok);
        }
        catch (NaoAutenticadoException)
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (AutorizacaoNegadaException)
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return (null, EstadoConsultaUi.Erro);
        }
    }

    private static RegistroIncidenteVm Mapear(ItemIncidente i) => new(
        i.Id, i.Codigo, AdminOpsUi.RotuloTipoIncidente(i.Tipo), i.Severidade.ToString(), AdminOpsUi.BadgeSeveridade(i.Severidade),
        AdminOpsUi.RotuloStatusIncidente(i.Status), AdminOpsUi.BadgeStatusIncidente(i.Status), i.Titulo, i.TenantAfetadoId,
        AdminOpsUi.RotuloRisco(i.ClassificacaoRisco), AdminOpsUi.BadgeRisco(i.ClassificacaoRisco), i.ResponsavelId,
        i.DetectadoEmUtc, i.AtualizadoEmUtc, i.EncerradoEmUtc);

    private static string? Limpar(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static T? ParseEnum<T>(string? valor) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(valor)
            && !string.Equals(valor, "Todos", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(valor, "Todas", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<T>(valor, ignoreCase: true, out var v)
            ? v
            : null;
}

// ---- Operações (observabilidade) --------------------------------------------------------------------

public interface IDadosOperacoes
{
    Task<(PainelOperacoesVm? Painel, EstadoConsultaUi Estado)> ObterPainelAsync(CancellationToken cancellationToken = default);
}

public sealed class DadosOperacoes : IDadosOperacoes
{
    private readonly ConsultaOperacoesAutorizada _consulta;

    public DadosOperacoes(ConsultaOperacoesAutorizada consulta)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        _consulta = consulta;
    }

    public async Task<(PainelOperacoesVm? Painel, EstadoConsultaUi Estado)> ObterPainelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var p = await _consulta.ObterPainelAsync(cancellationToken).ConfigureAwait(false);
            var workers = p.Workers.Select(Mapear).ToList();
            var integracoes = p.Integracoes.Select(Mapear).ToList();
            return (new PainelOperacoesVm(workers, integracoes, p.WorkersEstagnados, p.IntegracoesDegradadas, p.GeradoEmUtc)
            {
                ServicoWorker = p.ServicoWorker is { } servico ? ServicoWorkerVm.De(servico) : null,
            }, EstadoConsultaUi.Ok);
        }
        catch (NaoAutenticadoException)
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (AutorizacaoNegadaException)
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return (null, EstadoConsultaUi.Erro);
        }
    }

    private static RegistroWorkerVm Mapear(ItemSaudeWorker w)
    {
        var (rotulo, badge) = AdminOpsUi.EstadoWorker(w.Ativo, w.Estagnado);
        return new RegistroWorkerVm(w.Worker, w.Ativo, w.Estagnado, rotulo, badge, w.IniciadoEmUtc, w.UltimaBatidaUtc,
            w.UltimoSucessoUtc, w.TotalProcessado, w.UltimoErroUtc, w.UltimoErroCodigo, w.TotalErros, w.Reinicios);
    }

    private static RegistroIntegracaoOpVm Mapear(ItemIntegracao i)
    {
        var (rotulo, badge) = AdminOpsUi.EstadoIntegracao(i.Degradada);
        return new RegistroIntegracaoOpVm(i.Integracao, i.Degradada, rotulo, badge, i.UltimoSucessoUtc, i.UltimaFalhaUtc,
            i.UltimoErroCodigo, i.TotalSucesso, i.TotalFalha);
    }
}
