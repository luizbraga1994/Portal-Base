using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PortalBase.Application.Abstracoes;

namespace PortalBase.Infrastructure.Segredos;

/// <summary>
/// Decorador de cache EM MEMÓRIA para segredos (seção 11): TTL curto, nunca persistente, nunca
/// distribuído, nunca serializado nem registrado. Evita bater no cofre a cada operação sem criar
/// cache eterno; suporta invalidação para rotação (seção 12). O valor vive apenas no processo.
/// INT-002 (auditoria de 08/10/2026): UMA busca por vez por segredo (as leituras simultâneas esperam a mesma busca, em
/// vez de N chamadas ao cofre) e, se o cofre falhar com o TTL vencido, o valor anterior continua valendo por até
/// <see cref="ToleranciaPadrao"/> (nova tentativa a cada <see cref="IntervaloNovaTentativa"/>); passado isso, falha
/// (fail-closed). Sem valor anterior, a falha do cofre propaga.
/// </summary>
public sealed partial class ProvedorSegredosCache : IProvedorSegredos, IEscritorSegredos
{
    /// <summary>Por quanto tempo, depois do TTL, o valor anterior é servido com o cofre fora.</summary>
    public static readonly TimeSpan ToleranciaPadrao = TimeSpan.FromHours(1);

    /// <summary>Com o cofre fora, de quanto em quanto tempo uma leitura volta a tentar o cofre.</summary>
    public static readonly TimeSpan IntervaloNovaTentativa = TimeSpan.FromSeconds(30);

    private readonly IProvedorSegredos _interno;
    private readonly IRelogio _relogio;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _tolerancia;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Entrada> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buscas = new(StringComparer.Ordinal);

    public ProvedorSegredosCache(IProvedorSegredos interno, IRelogio relogio, TimeSpan ttl, ILogger<ProvedorSegredosCache>? logger = null, TimeSpan? tolerancia = null)
    {
        ArgumentNullException.ThrowIfNull(interno);
        ArgumentNullException.ThrowIfNull(relogio);
        _interno = interno;
        _relogio = relogio;
        _ttl = ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(300) : ttl;
        _tolerancia = tolerancia is { } t && t >= TimeSpan.Zero ? t : ToleranciaPadrao;
        _logger = logger;
    }

    public async ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencia);
        if (_cache.TryGetValue(referencia, out var entrada) && entrada.Expira > _relogio.AgoraUtc)
        {
            return entrada.Valor;
        }

        // Uma busca por vez por segredo: quem chega durante a busca espera e lê o resultado dela.
        var busca = _buscas.GetOrAdd(referencia, _ => new SemaphoreSlim(1, 1));
        await busca.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var agora = _relogio.AgoraUtc;
            var temAnterior = _cache.TryGetValue(referencia, out entrada);
            if (temAnterior && entrada.Expira > agora)
            {
                return entrada.Valor;
            }

            try
            {
                var valor = await _interno.ObterAsync(referencia, cancellationToken).ConfigureAwait(false);
                _cache[referencia] = new Entrada(valor, agora.Add(_ttl), agora.Add(_ttl).Add(_tolerancia));
                return valor;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && temAnterior && agora < entrada.UsavelAte)
            {
                // Cofre fora: segue com o valor lido antes (por tempo limitado) e só tenta de novo daqui a pouco.
                _cache[referencia] = entrada with { Expira = Min(agora.Add(IntervaloNovaTentativa), entrada.UsavelAte) };
                if (_logger is not null)
                {
                    LogValorAnterior(_logger, ex.GetType().Name, (int)(entrada.UsavelAte - agora).TotalMinutes);
                }

                return entrada.Valor;
            }
        }
        finally
        {
            busca.Release();
        }
    }

    /// <summary>
    /// Escreve no provedor interno (se ele suportar escrita) e INVALIDA o cache da referência para que a
    /// próxima leitura reflita o novo valor (rotação — seção 12). Fail-closed se o interno não for gravável.
    /// </summary>
    public async ValueTask DefinirAsync(string referencia, string valor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencia);
        if (_interno is not IEscritorSegredos gravavel)
        {
            throw new NotSupportedException("O provedor de segredos configurado não suporta escrita.");
        }

        await gravavel.DefinirAsync(referencia, valor, cancellationToken).ConfigureAwait(false);
        Invalidar(referencia);
    }

    /// <summary>Invalida uma referência (rotação de segredo — seção 12).</summary>
    public void Invalidar(string referencia) => _cache.TryRemove(referencia, out _);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    // Só o tipo do erro e o prazo: nunca a referência ou o valor do segredo (regra 8).
    [LoggerMessage(EventId = 7400, Level = LogLevel.Warning, Message = "Cofre de segredos indisponível ({TipoErro}): usando o valor lido antes por no máximo mais {Minutos} min. Confira o cofre.")]
    private static partial void LogValorAnterior(ILogger logger, string tipoErro, int minutos);

    private readonly record struct Entrada(string Valor, DateTimeOffset Expira, DateTimeOffset UsavelAte);
}
