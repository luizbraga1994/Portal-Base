using PortalBase.Application.Autorizacao;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Catalogo;

/// <summary>Base de um contribuinte e a versão de estrutura (última migration) que ela tem.</summary>
public sealed record BaseContribuinteSchema(Guid TenantId, string Nome, EstadoProvisionamento Estado, string? UltimaMigracao, bool Atualizada);

/// <summary>Situação da estrutura das bases: versão que o código espera e cada base provisionada.</summary>
public sealed record SituacaoSchemaContribuintes(string? VersaoEsperada, IReadOnlyList<BaseContribuinteSchema> Bases)
{
    public int Desatualizadas => Bases.Count(b => !b.Atualizada);
}

/// <summary>Resultado da atualização de UMA base.</summary>
public sealed record ResultadoAtualizacaoSchema(Guid TenantId, string Nome, bool Atualizada, string? Motivo);

/// <summary>
/// ATUALIZAÇÃO DA ESTRUTURA das bases dos contribuintes (migrations) depois de uma nova versão do portal — pela tela da
/// plataforma (<c>plataforma.tenant.provisionar</c>, decidido no servidor). Usa o MESMO provisionador idempotente e
/// fail-closed do cadastro (bloqueio por contribuinte, migrations, validação, versão registrada no catálogo e auditoria).
/// Contribuinte desabilitado não é tocado (atualiza ao ser reativado). Uma falha não interrompe as demais bases.
/// </summary>
public sealed class AtualizacaoSchemaContribuintes
{
    private const int MaximoBases = 500;

    private readonly IAutorizacaoPlataforma _plataforma;
    private readonly IContextoSeguranca _seguranca;
    private readonly IConsultaContribuintesPlataforma _consulta;
    private readonly IProvisionadorTenant _provisionador;
    private readonly IVersaoSchemaTenant _versao;

    public AtualizacaoSchemaContribuintes(
        IAutorizacaoPlataforma plataforma, IContextoSeguranca seguranca, IConsultaContribuintesPlataforma consulta,
        IProvisionadorTenant provisionador, IVersaoSchemaTenant versao)
    {
        ArgumentNullException.ThrowIfNull(plataforma);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(provisionador);
        ArgumentNullException.ThrowIfNull(versao);
        _plataforma = plataforma;
        _seguranca = seguranca;
        _consulta = consulta;
        _provisionador = provisionador;
        _versao = versao;
    }

    public async Task<SituacaoSchemaContribuintes> ObterSituacaoAsync(CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaTenantProvisionar, cancellationToken).ConfigureAwait(false);
        return await SituacaoAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Aplica as migrations pendentes em todas as bases ativas/prontas desatualizadas.</summary>
    public async Task<IReadOnlyList<ResultadoAtualizacaoSchema>> AtualizarDesatualizadasAsync(CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaTenantProvisionar, cancellationToken).ConfigureAwait(false);
        var solicitante = _seguranca.PrincipalId ?? throw new Excecoes.NaoAutenticadoException();
        var situacao = await SituacaoAsync(cancellationToken).ConfigureAwait(false);

        var resultados = new List<ResultadoAtualizacaoSchema>();
        foreach (var b in situacao.Bases.Where(b => !b.Atualizada))
        {
            if (b.Estado == EstadoProvisionamento.Desabilitado)
            {
                resultados.Add(new ResultadoAtualizacaoSchema(b.TenantId, b.Nome, false, "Desabilitado — a estrutura é atualizada ao reativar."));
                continue;
            }

            var r = await _provisionador.ProvisionarAsync(b.TenantId, solicitante, cancellationToken).ConfigureAwait(false);
            resultados.Add(new ResultadoAtualizacaoSchema(b.TenantId, b.Nome, r.Erro is null, r.Erro));
        }

        return resultados;
    }

    private async Task<SituacaoSchemaContribuintes> SituacaoAsync(CancellationToken cancellationToken)
    {
        var esperada = _versao.UltimaMigracaoDisponivel;
        var bases = (await _consulta.ListarAsync(MaximoBases, cancellationToken).ConfigureAwait(false))
            .Where(c => c.Estado is EstadoProvisionamento.Pronto or EstadoProvisionamento.Ativo or EstadoProvisionamento.Desabilitado)
            .Select(c => new BaseContribuinteSchema(c.Id, c.Nome, c.Estado, c.UltimaMigracao,
                esperada is null || string.Equals(c.UltimaMigracao, esperada, StringComparison.Ordinal)))
            .ToList();
        return new SituacaoSchemaContribuintes(esperada, bases);
    }
}
