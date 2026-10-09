using System.Collections.Concurrent;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>
/// Registro da ÚLTIMA INTEGRAÇÃO externa bem-sucedida (Prompt 31, seções 16, 44) — singleton EM MEMÓRIA. Os
/// clientes de APIs externas reportam sucesso/falha; a saúde é derivada do FRESCOR do último sucesso, SEM
/// disparar chamadas externas em cada health probe (§16/§17/§44). Nunca guarda credencial/token/payload (§3).
/// </summary>
public sealed class RegistroUltimaIntegracao : IRegistroUltimaIntegracao
{
    private sealed class Estado
    {
        public DateTimeOffset? UltimoSucessoUtc;
        public DateTimeOffset? UltimaFalhaUtc;
        public string? UltimoErroCodigo;
        public long TotalSucesso;
        public long TotalFalha;
    }

    private readonly ConcurrentDictionary<string, Estado> _estados = new(StringComparer.Ordinal);

    public void RegistrarSucesso(string integracao, DateTimeOffset agoraUtc)
    {
        var estado = _estados.GetOrAdd(Chave(integracao), _ => new Estado());
        lock (estado)
        {
            estado.UltimoSucessoUtc = agoraUtc;
            estado.TotalSucesso++;
        }
    }

    public void RegistrarFalha(string integracao, string codigoErro, DateTimeOffset agoraUtc)
    {
        var estado = _estados.GetOrAdd(Chave(integracao), _ => new Estado());
        lock (estado)
        {
            estado.UltimaFalhaUtc = agoraUtc;
            estado.UltimoErroCodigo = Limitar(codigoErro, 64);
            estado.TotalFalha++;
        }
    }

    public EstadoUltimaIntegracao? Obter(string integracao)
        => _estados.TryGetValue(Chave(integracao), out var estado) ? Copiar(integracao, estado) : null;

    public IReadOnlyList<EstadoUltimaIntegracao> Todos()
        => _estados.Select(par => Copiar(par.Key, par.Value)).OrderBy(e => e.Integracao, StringComparer.Ordinal).ToList();

    private static EstadoUltimaIntegracao Copiar(string integracao, Estado estado)
    {
        lock (estado)
        {
            return new EstadoUltimaIntegracao(integracao, estado.UltimoSucessoUtc, estado.UltimaFalhaUtc, estado.UltimoErroCodigo, estado.TotalSucesso, estado.TotalFalha);
        }
    }

    private static string Chave(string integracao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(integracao);
        return integracao.Trim();
    }

    private static string Limitar(string? valor, int max)
    {
        var limpo = (valor ?? "-").Trim();
        return limpo.Length > max ? limpo[..max] : limpo;
    }
}
