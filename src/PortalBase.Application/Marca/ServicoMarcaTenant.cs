using System.Globalization;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Marca;

namespace PortalBase.Application.Marca;

/// <summary>
/// Marca visual do CONTRIBUINTE (logo exibida na barra superior quando ele é o contexto ativo). A
/// LEITURA exige apenas um <see cref="ITenantContext"/> autorizado (o contribuinte já foi resolvido no
/// servidor a partir da sessão) — fail-closed sem tenant. A ALTERAÇÃO exige a permissão de administração
/// do tenant (<see cref="Permissoes.AdminTenant"/>) e é AUDITADA. Nunca cruza a fronteira A↔B (regra 5/6).
/// </summary>
public sealed class ServicoMarcaTenant
{
    private readonly IRepositorioMarcaTenant _repositorio;
    private readonly ITenantContext _tenant;
    private readonly IContextoSeguranca _seguranca;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly CacheResumoMarca _cache;

    public ServicoMarcaTenant(
        IRepositorioMarcaTenant repositorio,
        ITenantContext tenant,
        IContextoSeguranca seguranca,
        IServicoAutorizacao autorizacao,
        IAuditoria auditoria,
        IRelogio relogio,
        CacheResumoMarca cache)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(cache);
        _repositorio = repositorio;
        _tenant = tenant;
        _seguranca = seguranca;
        _autorizacao = autorizacao;
        _auditoria = auditoria;
        _relogio = relogio;
        _cache = cache;
    }

    /// <summary>Lê a logo do contribuinte ativo para servir. Fail-closed: sem tenant autorizado, nula.</summary>
    public async Task<MarcaVisualView?> ObterAsync(CancellationToken cancellationToken = default)
    {
        if (!_tenant.EstaAutorizado)
        {
            return null;
        }

        var marca = await _repositorio.ObterAsync(cancellationToken).ConfigureAwait(false);
        return marca is null ? null : new MarcaVisualView(marca.Conteudo, marca.ContentType, marca.HashSha256);
    }

    /// <summary>
    /// Só os metadados da logo do contribuinte ativo (UI-005), do cache curto POR CONTRIBUINTE (chave = o tenant
    /// autorizado, nunca um identificador do cliente) ou numa consulta sem os bytes. Fail-closed: sem tenant, nulo.
    /// </summary>
    public async Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default)
    {
        if (!_tenant.EstaAutorizado || _tenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        var chave = ChaveCache(tenantId);
        var agora = _relogio.AgoraUtc;
        if (_cache.TentarObter(chave, agora, out var emCache))
        {
            return emCache;
        }

        var resumo = await _repositorio.ObterResumoAsync(cancellationToken).ConfigureAwait(false);
        _cache.Definir(chave, resumo, agora);
        return resumo;
    }

    /// <summary>Chave da logo de um contribuinte no cache de resumo.</summary>
    public static string ChaveCache(Guid tenantId) => "tenant:" + tenantId.ToString("N");

    /// <summary>Define/atualiza a logo do contribuinte ativo. Exige <see cref="Permissoes.AdminTenant"/> + auditoria.</summary>
    public async Task DefinirAsync(byte[] conteudo, string contentType, CancellationToken cancellationToken = default)
    {
        if (!_tenant.EstaAutorizado || _tenant.TenantId is not Guid tenantId)
        {
            throw new TenantContextAusenteException();
        }

        await _autorizacao.ExigirAsync(tenantId, Permissoes.AdminTenant, cancellationToken).ConfigureAwait(false);

        if (!Guid.TryParse(_seguranca.PrincipalId, out var usuarioId))
        {
            throw new NaoAutenticadoException();
        }

        var agora = _relogio.AgoraUtc;
        var marca = await _repositorio.ObterAsync(cancellationToken).ConfigureAwait(false);
        if (marca is null)
        {
            marca = MarcaVisual.Criar(conteudo, contentType, agora, usuarioId);
        }
        else
        {
            marca.Atualizar(conteudo, contentType, agora, usuarioId);
        }

        await _repositorio.SalvarAsync(marca, cancellationToken).ConfigureAwait(false);
        _cache.Invalidar(ChaveCache(tenantId));

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(usuarioId.ToString(), tenantId.ToString(), AcaoAuditoria.MarcaTenantAtualizada, ResultadoAuditoria.Permitido, agora)
            {
                TipoRecurso = "MarcaVisualTenant",
                RecursoId = "logo",
                Metadados = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["contentType"] = marca.ContentType,
                    ["hash"] = marca.HashSha256,
                    ["tamanhoBytes"] = marca.TamanhoBytes.ToString(CultureInfo.InvariantCulture),
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
