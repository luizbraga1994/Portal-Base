using System.Globalization;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Domain.Arquivos;
using PortalBase.Domain.Excecoes;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// FONTES DE DADOS das telas do tenant. Cada serviço embrulha um read-model autorizado e mapeia o resultado/
// exceções para um estado de UI seguro (fail-closed, sem vazar detalhe). O tenant é o resolvido no servidor
// (SelecaoTenant.TenantAtual); NUNCA vem do cliente. Sem tenant → SemTenant; sem permissão → SemPermissao;
// erro → Erro.
// =====================================================================================================

/// <summary>Base das fontes de dados do tenant: fail-closed por tenant + mapeamento de exceção→estado (§27/§49).</summary>
public abstract class DadosTenantBase
{
    private readonly SelecaoTenant _selecao;

    protected DadosTenantBase(SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(selecao);
        _selecao = selecao;
    }

    protected bool SemTenant => _selecao.TenantAtual is null;

    /// <summary>Executa a consulta ao read-model mapeando o resultado/exceções para um estado de UI seguro.</summary>
    protected async Task<ResultadoListaVm<T>> ExecutarAsync<T>(
        FiltroPaginadoVm filtro,
        Func<Task<(IReadOnlyList<T> Itens, int Pagina, int Tamanho, long Total)>> consulta)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        ArgumentNullException.ThrowIfNull(consulta);

        if (SemTenant)
        {
            return ResultadoListaVm.Vazio<T>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.SemTenant);
        }

        try
        {
            var (itens, pagina, tamanho, total) = await consulta().ConfigureAwait(false);
            return new ResultadoListaVm<T>(itens, pagina, tamanho, total, EstadoConsultaUi.Ok);
        }
        catch (TenantContextAusenteException)
        {
            return ResultadoListaVm.Vazio<T>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.SemTenant);
        }
        catch (AutorizacaoNegadaException)
        {
            return ResultadoListaVm.Vazio<T>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            // Fail-closed e sem vazar detalhe interno (regra 16 / §27/§49).
            return ResultadoListaVm.Vazio<T>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.Erro);
        }
    }

    /// <summary>
    /// Executa uma consulta de DETALHE por Id (read-model de PRIMEIRA CLASSE) mapeando resultado/exceções para
    /// estado de UI seguro. <c>Ok + null</c> = "não encontrado" (a página decide a mensagem). Fail-closed.
    /// </summary>
    protected async Task<(T? Item, EstadoConsultaUi Estado)> ExecutarItemAsync<T>(Func<Task<T?>> consulta)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(consulta);

        if (SemTenant)
        {
            return (null, EstadoConsultaUi.SemTenant);
        }

        try
        {
            var item = await consulta().ConfigureAwait(false);
            return (item, EstadoConsultaUi.Ok);
        }
        catch (TenantContextAusenteException)
        {
            return (null, EstadoConsultaUi.SemTenant);
        }
        catch (AutorizacaoNegadaException)
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            // Fail-closed e sem vazar detalhe interno (regra 16 / §27/§49).
            return (null, EstadoConsultaUi.Erro);
        }
    }

    protected static string? Limpar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    protected static T? ParseEnum<T>(string? valor) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(valor)
            && !string.Equals(valor, "Todos", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(valor, "Todas", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<T>(valor, ignoreCase: true, out var v)
            ? v
            : null;
}

/// <summary>Formatação/badges das telas de Dados (cor + texto — nunca só cor).</summary>
public static class DadosUi
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    public static string HashCurto(string? hash) =>
        string.IsNullOrEmpty(hash) ? "—" : hash.Length <= 14 ? hash : hash[..14] + "…";

    public static string Tamanho(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString("N0", Br) + " B";
        }

        double kb = bytes / 1024d;
        if (kb < 1024)
        {
            return kb.ToString("N1", Br) + " KB";
        }

        return (kb / 1024d).ToString("N2", Br) + " MB";
    }

    public static string BadgeEstadoArquivo(EstadoArquivo e) => e switch
    {
        EstadoArquivo.Armazenado or EstadoArquivo.Processado => "ok",
        EstadoArquivo.Processando => "warn",
        EstadoArquivo.Rejeitado or EstadoArquivo.Falhado => "bad",
        EstadoArquivo.Expirado => "neutral",
        _ => "info",
    };

    public static string BadgeScan(ResultadoScan r) => r switch
    {
        ResultadoScan.Limpo => "ok",
        ResultadoScan.Infectado or ResultadoScan.Erro => "bad",
        ResultadoScan.Indisponivel => "warn",
        _ => "neutral",
    };
}

// ---- Arquivos ---------------------------------------------------------------------------------------

public interface IDadosArquivos
{
    Task<ResultadoListaVm<RegistroArquivoVm>> ConsultarAsync(FiltroArquivosVm filtro, CancellationToken cancellationToken = default);
}

public sealed class DadosArquivos : DadosTenantBase, IDadosArquivos
{
    private readonly ConsultaArquivosImportacaoAutorizada _consulta;

    public DadosArquivos(ConsultaArquivosImportacaoAutorizada consulta, SelecaoTenant selecao) : base(selecao)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        _consulta = consulta;
    }

    public Task<ResultadoListaVm<RegistroArquivoVm>> ConsultarAsync(FiltroArquivosVm filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        return ExecutarAsync<RegistroArquivoVm>(filtro, async () =>
        {
            var app = new FiltroArquivosImportacao
            {
                Desde = filtro.Desde,
                Ate = filtro.Ate,
                Estado = ParseEnum<EstadoArquivo>(filtro.Estado),
                ResultadoScan = ParseEnum<ResultadoScan>(filtro.ResultadoScan),
                Extensao = Limpar(filtro.Extensao),
                Sha256 = Limpar(filtro.Sha256),
                Pagina = filtro.Pagina,
                TamanhoPagina = filtro.TamanhoPagina,
            };
            var p = await _consulta.ConsultarAsync(app, cancellationToken).ConfigureAwait(false);
            var itens = p.Itens.Select(a => new RegistroArquivoVm(
                a.Id, a.NomeOriginalSanitizado, a.Extensao, a.ContentTypeDetectado, a.TamanhoBytes,
                DadosUi.Tamanho(a.TamanhoBytes), DadosUi.HashCurto(a.Sha256), a.Estado.ToString(),
                DadosUi.BadgeEstadoArquivo(a.Estado), a.ResultadoScan.ToString(), DadosUi.BadgeScan(a.ResultadoScan),
                a.EscaneadoEmUtc, a.MotivoRejeicao, a.EnviadoEmUtc, a.AtualizadoEmUtc)).ToList();
            return ((IReadOnlyList<RegistroArquivoVm>)itens, p.Pagina, p.TamanhoPagina, p.TotalItens);
        });
    }
}

