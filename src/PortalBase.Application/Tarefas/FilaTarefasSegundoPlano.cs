using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Tarefas;

namespace PortalBase.Application.Tarefas;

/// <summary>
/// ENFILEIRA e ACOMPANHA tarefas em segundo plano a partir da tela (ARQ-006). Fail-closed: contribuinte autorizado no
/// servidor e permissão NO contribuinte antes de gravar o pedido (a execução confere de novo, com a identidade de quem
/// pediu). O contribuinte e o solicitante da tarefa vêm do servidor — nunca do cliente (regras 3, 4, 5). Um pedido
/// igual a outro ainda ativo reaproveita o primeiro (idempotente). Só se acompanham tarefas do contribuinte atual.
/// </summary>
public sealed class FilaTarefasSegundoPlano
{
    private static readonly TimeSpan IntervaloConferencia = TimeSpan.FromSeconds(2);

    private readonly ITenantContext _tenant;
    private readonly IContextoSeguranca _seguranca;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IRepositorioTarefas _tarefas;
    private readonly SinalTarefas _sinal;
    private readonly IRelogio _relogio;

    public FilaTarefasSegundoPlano(
        ITenantContext tenant, IContextoSeguranca seguranca, IServicoAutorizacao autorizacao, IRepositorioTarefas tarefas,
        SinalTarefas sinal, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(tarefas);
        ArgumentNullException.ThrowIfNull(sinal);
        ArgumentNullException.ThrowIfNull(relogio);
        _tenant = tenant;
        _seguranca = seguranca;
        _autorizacao = autorizacao;
        _tarefas = tarefas;
        _sinal = sinal;
        _relogio = relogio;
    }

    /// <exception cref="TenantContextAusenteException">Sem contribuinte autorizado.</exception>
    /// <exception cref="AutorizacaoNegadaException">Sem <paramref name="permissao"/> no contribuinte.</exception>
    public async Task<SituacaoTarefa> EnfileirarAsync(
        TipoTarefaSegundoPlano tipo, string parametros, string chaveIdempotencia, string permissao, CancellationToken cancellationToken = default)
    {
        if (!_tenant.EstaAutorizado || _tenant.TenantId is not Guid tenantId)
        {
            throw new TenantContextAusenteException();
        }

        await _autorizacao.ExigirAsync(tenantId, permissao, cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(_seguranca.PrincipalId, out var solicitanteId))
        {
            throw new NaoAutenticadoException();
        }

        var ativa = await _tarefas.ObterAtivaAsync(tenantId, tipo, chaveIdempotencia, cancellationToken).ConfigureAwait(false);
        if (ativa is not null)
        {
            return SituacaoTarefa.De(ativa);
        }

        var tarefa = TarefaSegundoPlano.Criar(tipo, tenantId, solicitanteId, parametros, chaveIdempotencia, _relogio.AgoraUtc);
        await _tarefas.AdicionarAsync(tarefa, cancellationToken).ConfigureAwait(false);
        await _tarefas.SalvarAsync(cancellationToken).ConfigureAwait(false);
        _sinal.NovaTarefa();
        return SituacaoTarefa.De(tarefa);
    }

    /// <summary>Situação atual (do banco) de uma tarefa DO CONTRIBUINTE ATUAL; de outro, nula (fail-closed).</summary>
    public async Task<SituacaoTarefa?> ObterAsync(Guid tarefaId, CancellationToken cancellationToken = default)
    {
        if (!_tenant.EstaAutorizado || _tenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        var tarefa = await _tarefas.LerAsync(tarefaId, cancellationToken).ConfigureAwait(false);
        return tarefa is not null && tarefa.TenantId == tenantId ? SituacaoTarefa.De(tarefa) : null;
    }

    /// <summary>
    /// Espera a tarefa terminar por até <paramref name="tempo"/> (aviso do executor deste processo, conferindo o banco a
    /// cada 2 s) e devolve a situação mais recente — terminada ou ainda em andamento.
    /// </summary>
    public async Task<SituacaoTarefa?> AguardarAsync(Guid tarefaId, TimeSpan tempo, CancellationToken cancellationToken = default)
    {
        var limite = _relogio.AgoraUtc.Add(tempo);
        while (true)
        {
            var situacao = await ObterAsync(tarefaId, cancellationToken).ConfigureAwait(false);
            var restante = limite - _relogio.AgoraUtc;
            if (situacao is null || !situacao.Ativa || restante <= TimeSpan.Zero)
            {
                return situacao;
            }

            await _sinal.AguardarConclusaoAsync(tarefaId, restante < IntervaloConferencia ? restante : IntervaloConferencia, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
