using System.Buffers.Binary;
using System.Security.Cryptography;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Backup;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// Cifra o ARTEFATO de backup em repouso (Prompt 50, Fase 11) com AES-256-GCM em STREAMING por blocos.
/// A chave (32 bytes) é RESOLVIDA DO COFRE por referência (<see cref="OpcoesBackup.ChaveCriptografiaRef"/>)
/// e NUNCA é gravada junto ao backup (regra 7/11). Não é criptografia própria: é o AEAD do framework —
/// cada bloco carrega nonce aleatório + tag (autenticação detecta adulteração ⇒ corrupção falha o restore).
///
/// Formato do stream: repetição de blocos <c>[len(4, BE)][nonce(12)][tag(16)][ciphertext(len)]</c>,
/// terminado por um bloco de comprimento zero. Fail-closed: chave ausente/curta ⇒ erro (nunca texto claro).
/// </summary>
public sealed class CriptografiaBackupAesGcm : ICriptografiaBackup
{
    private const int TamanhoNonce = 12;   // 96 bits (recomendado para GCM)
    private const int TamanhoTag = 16;     // 128 bits
    private const int TamanhoChave = 32;   // AES-256
    private const int TamanhoBloco = 64 * 1024; // 64 KiB de texto claro por bloco

    private readonly IProvedorSegredos _segredos;
    private readonly OpcoesBackup _opcoes;

    public CriptografiaBackupAesGcm(IProvedorSegredos segredos, OpcoesBackup opcoes)
    {
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(opcoes);
        _segredos = segredos;
        _opcoes = opcoes;
    }

    public string ChaveRef => _opcoes.ChaveCriptografiaRef;

    public async Task CriptografarAsync(Stream claro, Stream cifradoDestino, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claro);
        ArgumentNullException.ThrowIfNull(cifradoDestino);

        var chave = await ObterChaveAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[TamanhoBloco];
        using var aes = new AesGcm(chave, TamanhoTag);

        while (true)
        {
            var lidos = await LerBlocoAsync(claro, buffer, cancellationToken).ConfigureAwait(false);
            if (lidos == 0)
            {
                await EscreverComprimentoAsync(cifradoDestino, 0, cancellationToken).ConfigureAwait(false); // fim
                break;
            }

            var nonce = RandomNumberGenerator.GetBytes(TamanhoNonce);
            var tag = new byte[TamanhoTag];
            var cifrado = new byte[lidos];
            aes.Encrypt(nonce, buffer.AsSpan(0, lidos), cifrado, tag);

            await EscreverComprimentoAsync(cifradoDestino, lidos, cancellationToken).ConfigureAwait(false);
            await cifradoDestino.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
            await cifradoDestino.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
            await cifradoDestino.WriteAsync(cifrado.AsMemory(0, lidos), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DecriptografarAsync(Stream cifrado, Stream claroDestino, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cifrado);
        ArgumentNullException.ThrowIfNull(claroDestino);

        var chave = await ObterChaveAsync(cancellationToken).ConfigureAwait(false);
        using var aes = new AesGcm(chave, TamanhoTag);
        var cabecalho = new byte[4];

        while (true)
        {
            if (!await LerExatoAsync(cifrado, cabecalho, cancellationToken).ConfigureAwait(false))
            {
                break; // EOF sem bloco final explícito
            }

            var comprimento = BinaryPrimitives.ReadInt32BigEndian(cabecalho);
            if (comprimento == 0)
            {
                break; // bloco final
            }

            if (comprimento is < 0 or > TamanhoBloco)
            {
                throw new CryptographicException("Bloco de backup malformado.");
            }

            var nonce = new byte[TamanhoNonce];
            var tag = new byte[TamanhoTag];
            var cifradoBloco = new byte[comprimento];
            if (!await LerExatoAsync(cifrado, nonce, cancellationToken).ConfigureAwait(false)
                || !await LerExatoAsync(cifrado, tag, cancellationToken).ConfigureAwait(false)
                || !await LerExatoAsync(cifrado, cifradoBloco, cancellationToken).ConfigureAwait(false))
            {
                throw new CryptographicException("Artefato de backup truncado.");
            }

            var claro = new byte[comprimento];
            aes.Decrypt(nonce, cifradoBloco, tag, claro); // tag inválida ⇒ CryptographicException (adulteração)
            await claroDestino.WriteAsync(claro.AsMemory(0, comprimento), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> LerBlocoAsync(Stream origem, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var lidos = await origem.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (lidos == 0)
            {
                break;
            }

            total += lidos;
        }

        return total;
    }

    private static async Task<bool> LerExatoAsync(Stream origem, byte[] destino, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destino.Length)
        {
            var lidos = await origem.ReadAsync(destino.AsMemory(total, destino.Length - total), cancellationToken).ConfigureAwait(false);
            if (lidos == 0)
            {
                return total == 0 ? false : throw new CryptographicException("Artefato de backup truncado.");
            }

            total += lidos;
        }

        return true;
    }

    private static async Task EscreverComprimentoAsync(Stream destino, int comprimento, CancellationToken cancellationToken)
    {
        var cabecalho = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(cabecalho, comprimento);
        await destino.WriteAsync(cabecalho, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ObterChaveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_opcoes.ChaveCriptografiaRef))
        {
            throw new InvalidOperationException("Backup:ChaveCriptografiaRef não configurada (chave de cifra dos backups).");
        }

        var base64 = await _segredos.ObterAsync(_opcoes.ChaveCriptografiaRef, cancellationToken).ConfigureAwait(false);
        var chave = Convert.FromBase64String(base64);
        if (chave.Length != TamanhoChave)
        {
            throw new InvalidOperationException("Chave de cifra de backup deve ter 32 bytes (AES-256).");
        }

        return chave;
    }
}
