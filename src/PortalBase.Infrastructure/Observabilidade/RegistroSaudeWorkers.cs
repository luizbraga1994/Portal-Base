using System.Collections.Concurrent;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>
/// Registro de SAÚDE dos WORKERS (Prompt 31, seções 20, 21) — singleton EM MEMÓRIA, thread-safe. Cada worker
/// reporta início/batida/sucesso/erro/encerramento; health checks e métricas leem daqui sem custo externo. É
/// per-processo (a saúde é exposta por métricas/health do próprio host do worker). Sem PII/payload (§3).
/// </summary>
public sealed class RegistroSaudeWorkers : IRegistroSaudeWorkers
{
    private sealed class Estado
    {
        public bool Ativo;
        public DateTimeOffset? IniciadoEmUtc;
        public DateTimeOffset? UltimaBatidaUtc;
        public DateTimeOffset? UltimoSucessoUtc;
        public long TotalProcessado;
        public DateTimeOffset? UltimoErroUtc;
        public string? UltimoErroCodigo;
        public long TotalErros;
        public int Reinicios;
    }

    private readonly ConcurrentDictionary<string, Estado> _estados = new(StringComparer.Ordinal);

    public void RegistrarInicio(string worker, DateTimeOffset agoraUtc)
    {
        var estado = _estados.GetOrAdd(Chave(worker), _ => new Estado());
        lock (estado)
        {
            if (estado.IniciadoEmUtc is not null && !estado.Ativo)
            {
                estado.Reinicios++;
            }

            estado.Ativo = true;
            estado.IniciadoEmUtc = agoraUtc;
            estado.UltimaBatidaUtc = agoraUtc;
        }
    }

    public void RegistrarBatida(string worker, DateTimeOffset agoraUtc)
    {
        var estado = _estados.GetOrAdd(Chave(worker), _ => new Estado());
        lock (estado)
        {
            estado.Ativo = true;
            estado.UltimaBatidaUtc = agoraUtc;
        }
    }

    public void RegistrarSucesso(string worker, long itensProcessados, DateTimeOffset agoraUtc)
    {
        var estado = _estados.GetOrAdd(Chave(worker), _ => new Estado());
        lock (estado)
        {
            estado.Ativo = true;
            estado.UltimaBatidaUtc = agoraUtc;
            estado.UltimoSucessoUtc = agoraUtc;
            estado.TotalProcessado += Math.Max(0, itensProcessados);
        }
    }

    public void RegistrarErro(string worker, string codigoErro, DateTimeOffset agoraUtc)
    {
        var estado = _estados.GetOrAdd(Chave(worker), _ => new Estado());
        lock (estado)
        {
            estado.Ativo = true;
            estado.UltimaBatidaUtc = agoraUtc;
            estado.UltimoErroUtc = agoraUtc;
            estado.UltimoErroCodigo = Limitar(codigoErro, 64);
            estado.TotalErros++;
        }
    }

    public void RegistrarEncerramento(string worker, DateTimeOffset agoraUtc)
    {
        if (_estados.TryGetValue(Chave(worker), out var estado))
        {
            lock (estado)
            {
                estado.Ativo = false;
                estado.UltimaBatidaUtc = agoraUtc;
            }
        }
    }

    public EstadoSaudeWorker? Obter(string worker)
        => _estados.TryGetValue(Chave(worker), out var estado) ? Copiar(worker, estado) : null;

    public IReadOnlyList<EstadoSaudeWorker> Todos()
        => _estados.Select(par => Copiar(par.Key, par.Value)).OrderBy(e => e.Worker, StringComparer.Ordinal).ToList();

    private static EstadoSaudeWorker Copiar(string worker, Estado estado)
    {
        lock (estado)
        {
            return new EstadoSaudeWorker(
                worker, estado.Ativo, estado.IniciadoEmUtc, estado.UltimaBatidaUtc, estado.UltimoSucessoUtc,
                estado.TotalProcessado, estado.UltimoErroUtc, estado.UltimoErroCodigo, estado.TotalErros, estado.Reinicios);
        }
    }

    private static string Chave(string worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        return worker.Trim();
    }

    private static string Limitar(string? valor, int max)
    {
        var limpo = (valor ?? "-").Trim();
        return limpo.Length > max ? limpo[..max] : limpo;
    }
}
