using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Marca;

namespace PortalBase.Application.Marca;

/// <summary>
/// Marca visual GLOBAL do portal (logo padrão exibida no login e como fallback). A LEITURA é pública
/// (a logo aparece antes da autenticação) — sem autorização. A ALTERAÇÃO é de PLATAFORMA: exige
/// <see cref="Permissoes.MarcaPlataformaAdministrar"/> (fail-closed) e é AUDITADA. Nunca registra o
/// binário — só o hash e o tamanho (regra 8/9).
/// </summary>
public sealed class ServicoMarcaPlataforma
{
    private readonly IRepositorioMarcaPlataforma _repositorio;
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IContextoSeguranca _seguranca;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly CacheResumoMarca _cache;

    /// <summary>Chave da logo global no cache de resumo.</summary>
    public const string ChaveCache = "plataforma";

    public ServicoMarcaPlataforma(
        IRepositorioMarcaPlataforma repositorio,
        IAutorizacaoPlataforma autorizacao,
        IContextoSeguranca seguranca,
        IAuditoria auditoria,
        IRelogio relogio,
        CacheResumoMarca cache)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(cache);
        _repositorio = repositorio;
        _autorizacao = autorizacao;
        _seguranca = seguranca;
        _auditoria = auditoria;
        _relogio = relogio;
        _cache = cache;
    }

    /// <summary>Lê a logo global para servir (público — pré-autenticação). Nula quando não configurada.</summary>
    public async Task<MarcaVisualView?> ObterAsync(CancellationToken cancellationToken = default)
    {
        var marca = await _repositorio.ObterParaExibicaoAsync(cancellationToken).ConfigureAwait(false);
        return marca is null ? null : new MarcaVisualView(marca.Conteudo, marca.ContentType, marca.HashSha256);
    }

    /// <summary>
    /// Só os metadados da logo global, do cache curto ou numa consulta sem os bytes (UI-005): o que os layouts e o
    /// <c>304 Not Modified</c> precisam. Nulo quando não há logo.
    /// </summary>
    public async Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default)
    {
        var agora = _relogio.AgoraUtc;
        if (_cache.TentarObter(ChaveCache, agora, out var emCache))
        {
            return emCache;
        }

        var resumo = await _repositorio.ObterResumoAsync(cancellationToken).ConfigureAwait(false);
        _cache.Definir(ChaveCache, resumo, agora);
        return resumo;
    }

    /// <summary>Define/atualiza a logo global. Exige administrador de PLATAFORMA (fail-closed) + auditoria.</summary>
    public async Task DefinirAsync(byte[] conteudo, string contentType, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.MarcaPlataformaAdministrar, cancellationToken).ConfigureAwait(false);

        var principal = _seguranca.PrincipalId;
        Guid? usuarioId = Guid.TryParse(principal, out var g) ? g : null;
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
        _cache.Invalidar(ChaveCache);

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(principal ?? "PLATAFORMA", "GLOBAL", AcaoAuditoria.MarcaPlataformaAtualizada, ResultadoAuditoria.Permitido, agora)
            {
                TipoRecurso = "MarcaVisualPlataforma",
                RecursoId = "global",
                Metadados = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["contentType"] = marca.ContentType,
                    ["hash"] = marca.HashSha256,
                    ["tamanhoBytes"] = marca.TamanhoBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
