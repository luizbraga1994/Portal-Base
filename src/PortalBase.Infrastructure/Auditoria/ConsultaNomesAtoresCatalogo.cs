using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Auditoria;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Auditoria;

/// <summary>
/// Nomes de exibição dos atores da trilha, lidos do catálogo de usuários. Consultas LINQ parametrizadas (regra 13) e
/// sempre limitadas; só projeta id, nome e login (nunca hash de senha, MFA ou outro dado da conta).
/// </summary>
public sealed class ConsultaNomesAtoresCatalogo : IConsultaNomesAtores
{
    /// <summary>Teto de identificadores traduzidos por chamada (uma página da trilha tem no máximo 200 registros).</summary>
    public const int MaximoIdentificadores = 200;

    // O catálogo usa colação que diferencia maiúsculas: a busca compara numa colação sem distinção de caixa e acento
    // ("joao" acha "João"). Com LIKE — no MySQL 8.4 o LOCATE (tradução de Contains) não ignora acentos nem com COLLATE.
    private const string ColacaoBusca = "utf8mb4_0900_ai_ci";
    // Escape do LIKE: "!" (a barra invertida é especial nos literais do MySQL e o provedor não a escapa em ESCAPE '...').
    private const string Escape = "!";

    private readonly PortalBaseCatalogoDbContext _contexto;

    public ConsultaNomesAtoresCatalogo(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<IReadOnlyDictionary<string, IdentificacaoAtor>> IdentificarAsync(
        IReadOnlyCollection<string> principalIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principalIds);
        var ids = principalIds
            .Select(p => Guid.TryParse(p, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(MaximoIdentificadores)
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, IdentificacaoAtor>(StringComparer.OrdinalIgnoreCase);
        }

        var usuarios = await _contexto.Usuarios.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Nome, u.Login })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return usuarios.ToDictionary(
            u => u.Id.ToString(),
            u => new IdentificacaoAtor(u.Nome, u.Login),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> IdsPorTextoAsync(string texto, int maximo, CancellationToken cancellationToken = default)
    {
        var termo = texto?.Trim() ?? string.Empty;
        if (termo.Length < 2 || maximo < 1)
        {
            return [];
        }

        // Padrão parametrizado; os curingas digitados (% e _) valem como texto.
        var padrao = "%" + termo.Replace(Escape, Escape + Escape, StringComparison.Ordinal)
            .Replace("%", Escape + "%", StringComparison.Ordinal)
            .Replace("_", Escape + "_", StringComparison.Ordinal) + "%";
        var ids = await _contexto.Usuarios.AsNoTracking()
            .Where(u => EF.Functions.Like(EF.Functions.Collate(u.Nome, ColacaoBusca), padrao, Escape)
                || EF.Functions.Like(EF.Functions.Collate(u.Login, ColacaoBusca), padrao, Escape)
                || EF.Functions.Like(EF.Functions.Collate(u.Email, ColacaoBusca), padrao, Escape))
            .OrderBy(u => u.Nome)
            .Select(u => u.Id)
            .Take(maximo)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ids.Select(id => id.ToString()).ToList();
    }
}
