using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Incidentes;

/// <summary>
/// Caso de uso de GOVERNANÇA DE INCIDENTES (Prompt 30, seções 45-47) — registrar, analisar, conter, classificar
/// (risco ao titular — §46, decisão HUMANA), preservar evidências (§47) e encerrar. Autorização SERVER-SIDE de
/// PLATAFORMA (deny-by-default + MFA quando configurado — §8/§30); toda operação é AUDITADA (sem payload/PII —
/// §22/§41). É a infraestrutura técnica para detectar/registrar/preservar/classificar/acompanhar; NÃO conclui
/// juridicamente nem comunica a ANPD (§46).
/// </summary>
public sealed class ServicoIncidentes
{
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRepositorioIncidentes _repositorio;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public ServicoIncidentes(
        IAutorizacaoPlataforma autorizacao, IContextoSeguranca seguranca, IRepositorioIncidentes repositorio, IAuditoria auditoria, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        _autorizacao = autorizacao;
        _seguranca = seguranca;
        _repositorio = repositorio;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    public async Task<IncidenteSeguranca> RegistrarAsync(NovoIncidente dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);
        var ator = await AutorizarAsync(cancellationToken).ConfigureAwait(false);
        var incidente = IncidenteSeguranca.Registrar(
            dados.Codigo, dados.TenantAfetadoId, dados.Tipo, dados.Severidade, dados.Titulo, dados.Descricao,
            dados.SistemasAfetados, dados.CategoriasDados, ator, dados.CorrelationId, _relogio.AgoraUtc);
        await _repositorio.AdicionarAsync(incidente, cancellationToken).ConfigureAwait(false);
        await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.IncidenteRegistrado, ator, incidente, cancellationToken).ConfigureAwait(false);
        return incidente;
    }

    public async Task IniciarAnaliseAsync(Guid id, string? nota, CancellationToken cancellationToken = default)
    {
        var (ator, incidente) = await CarregarAsync(id, cancellationToken).ConfigureAwait(false);
        incidente.IniciarAnalise(ator, nota, _relogio.AgoraUtc);
        await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.IncidenteAtualizado, ator, incidente, cancellationToken).ConfigureAwait(false);
    }

    public async Task RegistrarContencaoAsync(Guid id, string medida, CancellationToken cancellationToken = default)
    {
        var (ator, incidente) = await CarregarAsync(id, cancellationToken).ConfigureAwait(false);
        incidente.RegistrarContencao(ator, medida, _relogio.AgoraUtc);
        await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.IncidenteAtualizado, ator, incidente, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Classifica o risco/dano ao titular (§46) — decisão HUMANA (o chamador autorizado fornece).</summary>
    public async Task ClassificarRiscoTitularAsync(Guid id, bool envolveDadosPessoais, ClassificacaoRiscoTitular classificacao, string justificativa, string responsavelId, CancellationToken cancellationToken = default)
    {
        var (ator, incidente) = await CarregarAsync(id, cancellationToken).ConfigureAwait(false);
        incidente.ClassificarRiscoTitular(ator, envolveDadosPessoais, classificacao, justificativa, responsavelId, _relogio.AgoraUtc);
        await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.IncidenteClassificado, ator, incidente, cancellationToken).ConfigureAwait(false);
    }

    public async Task AssociarEvidenciasAsync(Guid id, string hash, string referencia, CancellationToken cancellationToken = default)
    {
        var (ator, incidente) = await CarregarAsync(id, cancellationToken).ConfigureAwait(false);
        incidente.AssociarEvidencias(ator, hash, referencia, _relogio.AgoraUtc);
        await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.IncidenteAtualizado, ator, incidente, cancellationToken).ConfigureAwait(false);
    }

    public async Task AdicionarNotaAsync(Guid id, string nota, CancellationToken cancellationToken = default)
    {
        var (ator, incidente) = await CarregarAsync(id, cancellationToken).ConfigureAwait(false);
        incidente.AdicionarNota(ator, nota, _relogio.AgoraUtc);
        await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.IncidenteAtualizado, ator, incidente, cancellationToken).ConfigureAwait(false);
    }

    public async Task EncerrarAsync(Guid id, string resumo, CancellationToken cancellationToken = default)
    {
        var (ator, incidente) = await CarregarAsync(id, cancellationToken).ConfigureAwait(false);
        incidente.Encerrar(ator, resumo, _relogio.AgoraUtc);
        await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.IncidenteEncerrado, ator, incidente, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Ator, IncidenteSeguranca Incidente)> CarregarAsync(Guid id, CancellationToken cancellationToken)
    {
        var ator = await AutorizarAsync(cancellationToken).ConfigureAwait(false);
        var incidente = await _repositorio.ObterPorIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new EntradaInvalidaException("Incidente inexistente.");
        return (ator, incidente);
    }

    private async Task<string> AutorizarAsync(CancellationToken cancellationToken)
    {
        await _autorizacao.ExigirAsync(Permissoes.SegurancaIncidentesGerir, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_seguranca.PrincipalId))
        {
            throw new NaoAutenticadoException();
        }

        return _seguranca.PrincipalId!;
    }

    private async Task AuditarAsync(string acao, string ator, IncidenteSeguranca incidente, CancellationToken cancellationToken)
    {
        var dados = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["incidente"] = incidente.Codigo,
            ["tipo"] = incidente.Tipo.ToString(),
            ["severidade"] = incidente.Severidade.ToString(),
            ["status"] = incidente.Status.ToString(),
            ["tenantAfetado"] = incidente.TenantAfetadoId?.ToString("N") ?? "-",
        };

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(ator, incidente.TenantAfetadoId?.ToString() ?? "plataforma", acao, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = "IncidenteSeguranca",
                RecursoId = incidente.Id.ToString("N"),
                Metadados = dados,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
