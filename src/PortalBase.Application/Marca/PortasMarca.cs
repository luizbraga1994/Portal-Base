using PortalBase.Domain.Marca;

namespace PortalBase.Application.Marca;

/// <summary>Projeção da logo para SERVIR (bytes + tipo + hash para ETag/cache). Nula quando não configurada.</summary>
public sealed record MarcaVisualView(byte[] Conteudo, string ContentType, string HashSha256);

/// <summary>
/// Só os METADADOS da logo, sem os bytes (UI-005, auditoria de 08/10/2026): basta para saber se existe e para o ETag.
/// Antes, o layout lia a imagem inteira (até 10 MB) do banco a cada navegação só para saber se havia logo.
/// </summary>
public sealed record ResumoMarcaVisual(string ContentType, string HashSha256, long TamanhoBytes);

/// <summary>
/// Cache CURTO (por processo) do resumo da logo — a global e a de cada contribuinte, por chave. Guarda também a
/// AUSÊNCIA (sem logo). Invalidado na alteração neste processo; outra instância vê a mudança em até <see cref="Validade"/>.
/// Só metadados (nunca os bytes, nunca dado do contribuinte além do hash da imagem).
/// </summary>
public sealed class CacheResumoMarca
{
    public static readonly TimeSpan Validade = TimeSpan.FromSeconds(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ResumoMarcaVisual? Resumo, DateTimeOffset Expira)> _itens =
        new(StringComparer.Ordinal);

    public bool TentarObter(string chave, DateTimeOffset agoraUtc, out ResumoMarcaVisual? resumo)
    {
        if (_itens.TryGetValue(chave, out var item) && item.Expira > agoraUtc)
        {
            resumo = item.Resumo;
            return true;
        }

        resumo = null;
        return false;
    }

    public void Definir(string chave, ResumoMarcaVisual? resumo, DateTimeOffset agoraUtc) => _itens[chave] = (resumo, agoraUtc.Add(Validade));

    public void Invalidar(string chave) => _itens.TryRemove(chave, out _);
}

/// <summary>Repositório da logo GLOBAL do portal, no CATÁLOGO (linha única). Nunca guarda segredo (regra 7).</summary>
public interface IRepositorioMarcaPlataforma
{
    /// <summary>Lê a logo RASTREADA na unidade de trabalho da requisição — usada pela escrita (atualizar + salvar).</summary>
    Task<MarcaVisual?> ObterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lê a logo só para EXIBIÇÃO (sem rastreamento, em contexto próprio e curto). Usada pelos layouts, que
    /// inicializam CONCORRENTEMENTE com a página no SSR do Blazor — não pode disputar o contexto da requisição.
    /// </summary>
    Task<MarcaVisual?> ObterParaExibicaoAsync(CancellationToken cancellationToken = default);

    /// <summary>Só os metadados (sem os bytes), em contexto próprio e curto como <see cref="ObterParaExibicaoAsync"/>.</summary>
    Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default);

    Task SalvarAsync(MarcaVisual marca, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repositório da logo do CONTRIBUINTE, no banco do tenant ATIVO (linha única, escopo implícito de
/// tenant — resolvido no servidor). Nunca cruza a fronteira entre bancos de contribuintes (regra 4/5).
/// </summary>
public interface IRepositorioMarcaTenant
{
    Task<MarcaVisual?> ObterAsync(CancellationToken cancellationToken = default);

    /// <summary>Só os metadados (sem os bytes).</summary>
    Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default);

    Task SalvarAsync(MarcaVisual marca, CancellationToken cancellationToken = default);
}
