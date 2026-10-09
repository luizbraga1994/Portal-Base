using PortalBase.Application.Administracao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// FACHADAS de leitura ADMINISTRATIVA/OPERACIONAL REAIS (Prompt 71). Cada fachada consome um read-model
// AUTORIZADO da Application (tenant) e mapeia sucesso/exceções para um
// estado de UI seguro (Ok/SemTenant/SemPermissao/Erro) — nunca vaza detalhe interno nem segredo.
// A tela apenas renderiza o estado; a autorização e o isolamento por tenant são do SERVIDOR.
// =====================================================================================================

/// <summary>Detalhe do tenant corrente com o estado de consulta para a UI.</summary>
public sealed record TenantAdminVm(EstadoConsultaUi Estado, DetalheTenant? Detalhe)
{
    public static readonly TenantAdminVm SemTenant = new(EstadoConsultaUi.SemTenant, null);
}

public interface IDadosTenantAdmin
{
    Task<TenantAdminVm> ObterAsync();
}

public sealed class DadosTenantAdmin : IDadosTenantAdmin
{
    private readonly IConsultaTenantAutorizada _consulta;
    private readonly SelecaoTenant _selecao;

    public DadosTenantAdmin(IConsultaTenantAutorizada consulta, SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(selecao);
        _consulta = consulta;
        _selecao = selecao;
    }

    public async Task<TenantAdminVm> ObterAsync()
    {
        if (_selecao.TenantAtual is null)
        {
            return TenantAdminVm.SemTenant;
        }

        try
        {
            var detalhe = await _consulta.ObterAtualAsync().ConfigureAwait(false);
            return detalhe is null ? TenantAdminVm.SemTenant : new TenantAdminVm(EstadoConsultaUi.Ok, detalhe);
        }
        catch (Exception excecao) when (AdminUi.EhAutorizacao(excecao))
        {
            return new TenantAdminVm(EstadoConsultaUi.SemPermissao, null);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return new TenantAdminVm(EstadoConsultaUi.Erro, null);
        }
    }
}

/// <summary>Rótulos/badges de apresentação da administração real (Prompt 71) — só apresentação.</summary>
public static class AdminUi
{
    /// <summary>Verdadeiro se a exceção é de autorização/autenticação (mapeia para "Sem permissão").</summary>
    public static bool EhAutorizacao(Exception excecao) =>
        excecao is AutorizacaoNegadaException or NaoAutenticadoException or TenantNaoAutorizadoException or MfaRequeridoException;

    /// <summary>
    /// Resumo curto da versão de schema para o cartão: a data da migração ("20260918214026_TenantMarcaVisual" →
    /// "18/09/2026"); o nome completo continua na tabela. Formato inesperado: o próprio texto, ou "—".
    /// </summary>
    public static string ResumoSchema(string? versao)
    {
        if (string.IsNullOrWhiteSpace(versao))
        {
            return "—";
        }

        return versao.Length >= 14
            && DateTime.TryParseExact(versao[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var quando)
            ? quando.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : versao;
    }

    public static (string Rotulo, string Badge) EstadoTenant(EstadoProvisionamento estado) => estado switch
    {
        EstadoProvisionamento.Ativo => ("Ativo", "ok"),
        EstadoProvisionamento.Pronto => ("Pronto", "ok"),
        EstadoProvisionamento.Provisionando => ("Provisionando", "warn"),
        EstadoProvisionamento.Pendente => ("Pendente", "warn"),
        EstadoProvisionamento.Falhado => ("Falhado", "bad"),
        EstadoProvisionamento.Desabilitado => ("Desabilitado", "neutral"),
        _ => (estado.ToString(), "neutral"),
    };

}
