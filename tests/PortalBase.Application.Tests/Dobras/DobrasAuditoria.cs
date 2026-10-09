using PortalBase.Application.Auditoria;

namespace PortalBase.Application.Tests.Dobras;

/// <summary>Consulta de auditoria fake: captura o filtro recebido e devolve os <see cref="Itens"/> configurados.</summary>
internal sealed class ConsultaAuditoriaFake : IConsultaAuditoria
{
    public FiltroAuditoria? UltimoFiltro { get; private set; }

    public List<RegistroAuditoriaDto> Itens { get; } = [];

    public Task<PaginaAuditoria> ConsultarAsync(FiltroAuditoria filtro, CancellationToken cancellationToken = default)
    {
        UltimoFiltro = filtro;
        return Task.FromResult(new PaginaAuditoria([.. Itens], filtro.Pagina, filtro.TamanhoPagina, Itens.Count));
    }
}

/// <summary>Nomes de atores fake: um "catálogo" em memória de id → (nome, login).</summary>
internal sealed class ConsultaNomesAtoresFake : IConsultaNomesAtores
{
    public Dictionary<string, IdentificacaoAtor> Usuarios { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<IReadOnlyCollection<string>> Pedidos { get; } = [];

    public Task<IReadOnlyDictionary<string, IdentificacaoAtor>> IdentificarAsync(IReadOnlyCollection<string> principalIds, CancellationToken cancellationToken = default)
    {
        Pedidos.Add(principalIds);
        IReadOnlyDictionary<string, IdentificacaoAtor> achados = Usuarios
            .Where(u => principalIds.Contains(u.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(u => u.Key, u => u.Value, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(achados);
    }

    public Task<IReadOnlyList<string>> IdsPorTextoAsync(string texto, int maximo, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> ids = Usuarios
            .Where(u => u.Value.Nome.Contains(texto, StringComparison.OrdinalIgnoreCase) || u.Value.Login.Contains(texto, StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Key)
            .Take(maximo)
            .ToList();
        return Task.FromResult(ids);
    }
}
