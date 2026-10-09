using System.Globalization;
using PortalBase.Application.Administracao;
using PortalBase.Application.Provisionamento;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// AÇÕES administrativas REAIS (Prompt 72): ciclo de vida do contribuinte (CicloVidaTenant). COMANDO autorizado no
// SERVIDOR, tenant-scoped, idempotente/validado por estado e auditado. A UI só dispara e mapeia o resultado.
// =====================================================================================================

// ---- Ciclo de vida do contribuinte (Ativar / Desabilitar / Reativar) --------------------------------

public interface IDadosAcoesTenant
{
    Task<ResultadoAcaoVm> AtivarAsync(CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> DesabilitarAsync(CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> ReativarAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Grava os dados cadastrais editáveis do contribuinte selecionado (admin.tenant no servidor; CNPJ e nome não mudam).
    /// <paramref name="atualizadoEmVisto"/> = a data de atualização que a tela mostrava (concorrência otimista).
    /// </summary>
    Task<ResultadoAcaoVm> AtualizarCadastroAsync(DadosCadastroTenant dados, DateTimeOffset atualizadoEmVisto, CancellationToken cancellationToken = default);
}

public sealed class DadosAcoesTenant : IDadosAcoesTenant
{
    private readonly CicloVidaTenant _ciclo;
    private readonly EdicaoCadastroTenant _edicao;
    private readonly SelecaoTenant _selecao;

    public DadosAcoesTenant(CicloVidaTenant ciclo, EdicaoCadastroTenant edicao, SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(ciclo);
        ArgumentNullException.ThrowIfNull(edicao);
        ArgumentNullException.ThrowIfNull(selecao);
        _ciclo = ciclo;
        _edicao = edicao;
        _selecao = selecao;
    }

    public async Task<ResultadoAcaoVm> AtualizarCadastroAsync(
        DadosCadastroTenant dados, DateTimeOffset atualizadoEmVisto, CancellationToken cancellationToken = default)
    {
        if (_selecao.TenantAtual is null)
        {
            return MapeadorAcaoAdmin.SemTenant();
        }

        try
        {
            var r = await _edicao.AtualizarAsync(dados, atualizadoEmVisto, cancellationToken).ConfigureAwait(false);
            if (!r.Sucesso)
            {
                return new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Dados não salvos", r.Erro ?? "Confira os campos e tente de novo.");
            }

            return r.Alterado
                ? new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Dados do contribuinte salvos", "As alterações foram gravadas e registradas na auditoria.")
                : new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Nada para salvar", "Os dados já estavam assim.");
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }

    public Task<ResultadoAcaoVm> AtivarAsync(CancellationToken cancellationToken = default) =>
        ExecutarAsync((tid, ct) => _ciclo.AtivarAsync(tid, ct), "Contribuinte ativado", cancellationToken);

    public Task<ResultadoAcaoVm> DesabilitarAsync(CancellationToken cancellationToken = default) =>
        ExecutarAsync((tid, ct) => _ciclo.DesabilitarAsync(tid, ct), "Contribuinte desabilitado", cancellationToken);

    public Task<ResultadoAcaoVm> ReativarAsync(CancellationToken cancellationToken = default) =>
        ExecutarAsync((tid, ct) => _ciclo.ReativarAsync(tid, ct), "Contribuinte reativado", cancellationToken);

    private async Task<ResultadoAcaoVm> ExecutarAsync(
        Func<Guid, CancellationToken, Task<ResultadoCicloVida>> operacao,
        string tituloSucesso,
        CancellationToken cancellationToken)
    {
        if (_selecao.TenantAtual is not Guid tenantId)
        {
            return MapeadorAcaoAdmin.SemTenant();
        }

        try
        {
            var r = await operacao(tenantId, cancellationToken).ConfigureAwait(false);
            if (r.Sucesso)
            {
                // Idempotente no domínio: se já estava no estado alvo, o comando devolve sucesso sem alterar nada.
                return new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, tituloSucesso, $"Estado atual do contribuinte: {r.Estado}.");
            }

            // Transição não aplicável ao estado atual (ex.: ativar um tenant não-pronto). NADA foi alterado.
            return new ResultadoAcaoVm(false, EstadoAcaoUi.Conflito, "Transição não aplicável",
                r.Motivo ?? "O estado atual do contribuinte não permite esta transição.");
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }
}
