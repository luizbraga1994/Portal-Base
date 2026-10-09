using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Segredos;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Segredos;

/// <summary>
/// Acesso isolado a segredos por tenant (seção 24). A permissão exigida e o NOME do segredo
/// são determinados no servidor a partir do <b>tipo</b> e do <b>tenant autorizado</b> — um
/// cliente não escolhe a referência e não alcança o segredo de outro tenant. Cada acesso
/// bem-sucedido é auditado (<c>SecretAccessed</c>) sem registrar o valor.
/// </summary>
public sealed class AcessoSegredoTenant : IAcessoSegredoTenant
{
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IProvedorSegredos _segredos;
    private readonly IEscritorSegredos _escritor;
    private readonly IContextoSeguranca _seguranca;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly OpcoesVault _opcoes;

    public AcessoSegredoTenant(
        IServicoAutorizacao autorizacao,
        IProvedorSegredos segredos,
        IEscritorSegredos escritor,
        IContextoSeguranca seguranca,
        IAuditoria auditoria,
        IRelogio relogio,
        IOptions<OpcoesVault> opcoes)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(escritor);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);

        _autorizacao = autorizacao;
        _segredos = segredos;
        _escritor = escritor;
        _seguranca = seguranca;
        _auditoria = auditoria;
        _relogio = relogio;
        _opcoes = opcoes.Value;
    }

    public async Task<string> ObterAsync(Guid tenantId, TipoSegredoTenant tipo, CancellationToken cancellationToken = default)
    {
        var (permissao, nome) = Resolver(tipo, tenantId);

        // Autoriza server-side (permissão + tenant + MFA quando de alto impacto). Deny → ExigirAsync
        // audita a negação e lança (fail-closed) — o segredo nunca é buscado.
        await _autorizacao.ExigirAsync(tenantId, permissao, cancellationToken).ConfigureAwait(false);

        var valor = await _segredos.ObterAsync(nome, cancellationToken).ConfigureAwait(false);

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId.ToString(), AcaoAuditoria.SecretAccessed, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = "Segredo",
                RecursoId = nome, // nome lógico, NUNCA o valor
            },
            cancellationToken).ConfigureAwait(false);

        return valor;
    }

    public async Task DefinirAsync(Guid tenantId, TipoSegredoTenant tipo, string valor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(valor);
        var (permissao, nome) = Resolver(tipo, tenantId);

        // Autoriza server-side (permissão + tenant + MFA quando de alto impacto). Deny → fail-closed.
        await _autorizacao.ExigirAsync(tenantId, permissao, cancellationToken).ConfigureAwait(false);

        // Grava SOMENTE no cofre (nunca banco/log). Fail-closed se o cofre não suportar escrita.
        await _escritor.DefinirAsync(nome, valor, cancellationToken).ConfigureAwait(false);

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId.ToString(), AcaoAuditoria.SecretUpdated, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = "Segredo",
                RecursoId = nome, // nome lógico, NUNCA o valor
            },
            cancellationToken).ConfigureAwait(false);
    }

    public string NomeDoSegredo(Guid tenantId, TipoSegredoTenant tipo) => Resolver(tipo, tenantId).Nome;

    private (string Permissao, string Nome) Resolver(TipoSegredoTenant tipo, Guid tenantId)
    {
        var ambiente = _opcoes.Ambiente;
        return tipo switch
        {
            // Segredo de integração externa do tenant (ex.: client_secret de uma API) — gerido pelo admin do tenant.
            TipoSegredoTenant.SegredoIntegracao => (Permissoes.AdminTenant, NomeSegredo.SegredoIntegracao(ambiente, tenantId)),
            // Acrescente aqui os segredos do seu portal: cada tipo define a permissão exigida e o nome lógico no cofre.
            _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo de segredo desconhecido."),
        };
    }
}
