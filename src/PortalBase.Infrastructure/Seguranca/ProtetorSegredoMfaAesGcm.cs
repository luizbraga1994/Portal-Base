using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Seguranca;

/// <summary>
/// Proteção do segredo TOTP em repouso com AES-256-GCM (AEAD autenticado), chave no cofre
/// (seção 18). O banco guarda somente <c>nonce | tag | ciphertext</c> em Base64 — nunca o
/// segredo em claro. Nonce aleatório por operação; a autenticação (tag) detecta adulteração.
/// Envelope de cifra padrão sobre primitiva do framework — nenhum algoritmo próprio (seção 5).
/// </summary>
public sealed class ProtetorSegredoMfaAesGcm : IProtetorSegredoMfa
{
    private const int TamanhoNonce = 12; // 96 bits (recomendado para GCM)
    private const int TamanhoTag = 16;   // 128 bits
    private const int TamanhoChave = 32; // AES-256

    private readonly IProvedorSegredos _segredos;
    private readonly OpcoesSegurancaIdentidade _opcoes;

    public ProtetorSegredoMfaAesGcm(IProvedorSegredos segredos, IOptions<OpcoesSegurancaIdentidade> opcoes)
    {
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(opcoes);
        _segredos = segredos;
        _opcoes = opcoes.Value;
    }

    public async ValueTask<string> ProtegerAsync(string segredoClaro, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(segredoClaro);
        var chave = await ObterChaveAsync(cancellationToken).ConfigureAwait(false);

        var textoClaro = Encoding.UTF8.GetBytes(segredoClaro);
        var nonce = RandomNumberGenerator.GetBytes(TamanhoNonce);
        var cifrado = new byte[textoClaro.Length];
        var tag = new byte[TamanhoTag];

        using (var aes = new AesGcm(chave, TamanhoTag))
        {
            aes.Encrypt(nonce, textoClaro, cifrado, tag);
        }

        var combinado = new byte[TamanhoNonce + TamanhoTag + cifrado.Length];
        Buffer.BlockCopy(nonce, 0, combinado, 0, TamanhoNonce);
        Buffer.BlockCopy(tag, 0, combinado, TamanhoNonce, TamanhoTag);
        Buffer.BlockCopy(cifrado, 0, combinado, TamanhoNonce + TamanhoTag, cifrado.Length);
        return Convert.ToBase64String(combinado);
    }

    public async ValueTask<string> DesprotegerAsync(string segredoProtegido, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(segredoProtegido);
        var chave = await ObterChaveAsync(cancellationToken).ConfigureAwait(false);

        var combinado = Convert.FromBase64String(segredoProtegido);
        if (combinado.Length < TamanhoNonce + TamanhoTag)
        {
            throw new CryptographicException("Segredo protegido malformado.");
        }

        var nonce = combinado.AsSpan(0, TamanhoNonce);
        var tag = combinado.AsSpan(TamanhoNonce, TamanhoTag);
        var cifrado = combinado.AsSpan(TamanhoNonce + TamanhoTag);
        var textoClaro = new byte[cifrado.Length];

        using (var aes = new AesGcm(chave, TamanhoTag))
        {
            aes.Decrypt(nonce, cifrado, tag, textoClaro);
        }

        return Encoding.UTF8.GetString(textoClaro);
    }

    private async ValueTask<byte[]> ObterChaveAsync(CancellationToken cancellationToken)
    {
        var referencia = _opcoes.ChaveProtecaoMfaRef;
        if (string.IsNullOrWhiteSpace(referencia))
        {
            throw new InvalidOperationException("SegurancaIdentidade:ChaveProtecaoMfaRef não configurada.");
        }

        var base64 = await _segredos.ObterAsync(referencia, cancellationToken).ConfigureAwait(false);
        var chave = Convert.FromBase64String(base64);
        if (chave.Length != TamanhoChave)
        {
            throw new InvalidOperationException("Chave de proteção MFA deve ter 32 bytes (AES-256).");
        }

        return chave;
    }
}
