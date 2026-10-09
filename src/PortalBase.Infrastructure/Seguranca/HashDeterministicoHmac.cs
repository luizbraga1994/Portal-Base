using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Seguranca;

/// <summary>
/// Hash determinístico (HMAC-SHA256 com chave do cofre) para tokens/códigos que precisam ser
/// localizados por igualdade (seções 27, 36). A chave (pepper) vem do cofre — o valor em claro
/// nunca é persistido. Determinístico de propósito: permite a busca do registro pelo hash.
/// </summary>
public sealed class HashDeterministicoHmac : IHashDeterministico
{
    private readonly IProvedorSegredos _segredos;
    private readonly OpcoesSegurancaIdentidade _opcoes;

    public HashDeterministicoHmac(IProvedorSegredos segredos, IOptions<OpcoesSegurancaIdentidade> opcoes)
    {
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(opcoes);
        _segredos = segredos;
        _opcoes = opcoes.Value;
    }

    public async ValueTask<string> HashAsync(string valor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(valor);

        var chave = await ObterChaveAsync(cancellationToken).ConfigureAwait(false);
        var hash = HMACSHA256.HashData(chave, Encoding.UTF8.GetBytes(valor));
        return Convert.ToHexString(hash);
    }

    private async ValueTask<byte[]> ObterChaveAsync(CancellationToken cancellationToken)
    {
        var referencia = _opcoes.ChaveHashRef;
        if (string.IsNullOrWhiteSpace(referencia))
        {
            throw new InvalidOperationException("SegurancaIdentidade:ChaveHashRef não configurada.");
        }

        var base64 = await _segredos.ObterAsync(referencia, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(base64);
    }
}
