using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Observabilidade;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>Linha de saúde de um worker no CATÁLOGO (uma por nome técnico). Sem PII/payload.</summary>
public sealed class SaudeWorkerPersistida
{
    public string Worker { get; set; } = null!;

    public bool Ativo { get; set; }

    public DateTimeOffset? IniciadoEmUtc { get; set; }

    public DateTimeOffset? UltimaBatidaUtc { get; set; }

    public DateTimeOffset? UltimoSucessoUtc { get; set; }

    public long TotalProcessado { get; set; }

    public DateTimeOffset? UltimoErroUtc { get; set; }

    public string? UltimoErroCodigo { get; set; }

    public long TotalErros { get; set; }

    public int Reinicios { get; set; }

    public DateTimeOffset GravadoEmUtc { get; set; }
}

/// <summary>
/// Saúde dos workers no catálogo: o host do Worker grava (upsert por nome) o snapshot do registro em memória a cada
/// batida; a Web lê. Banco CENTRAL (não é dado de contribuinte). Nunca grava PII/payload.
/// </summary>
public sealed class RepositorioSaudeWorkersPersistida : IRepositorioSaudeWorkersPersistida
{
    private const int LimiteCodigo = 128;

    private readonly PortalBaseCatalogoDbContext _contexto;

    public RepositorioSaudeWorkersPersistida(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task GravarAsync(IReadOnlyList<EstadoSaudeWorker> estados, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estados);
        var nomes = estados.Select(e => e.Worker).ToList();
        var existentes = await _contexto.SaudeWorkers.Where(x => nomes.Contains(x.Worker)).ToDictionaryAsync(x => x.Worker, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        foreach (var e in estados)
        {
            if (!existentes.TryGetValue(e.Worker, out var linha))
            {
                linha = new SaudeWorkerPersistida { Worker = e.Worker.Length > 64 ? e.Worker[..64] : e.Worker };
                _contexto.SaudeWorkers.Add(linha);
            }

            linha.Ativo = e.Ativo;
            linha.IniciadoEmUtc = e.IniciadoEmUtc;
            linha.UltimaBatidaUtc = e.UltimaBatidaUtc;
            linha.UltimoSucessoUtc = e.UltimoSucessoUtc;
            linha.TotalProcessado = e.TotalProcessado;
            linha.UltimoErroUtc = e.UltimoErroUtc;
            linha.UltimoErroCodigo = e.UltimoErroCodigo is { Length: > LimiteCodigo } c ? c[..LimiteCodigo] : e.UltimoErroCodigo;
            linha.TotalErros = e.TotalErros;
            linha.Reinicios = e.Reinicios;
            linha.GravadoEmUtc = agoraUtc;
        }

        await _contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EstadoSaudeWorker>> ListarAsync(CancellationToken cancellationToken = default)
        => await _contexto.SaudeWorkers.AsNoTracking()
            .OrderBy(x => x.Worker)
            .Select(x => new EstadoSaudeWorker(x.Worker, x.Ativo, x.IniciadoEmUtc, x.UltimaBatidaUtc, x.UltimoSucessoUtc,
                x.TotalProcessado, x.UltimoErroUtc, x.UltimoErroCodigo, x.TotalErros, x.Reinicios))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
