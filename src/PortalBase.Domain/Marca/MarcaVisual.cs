using System.Security.Cryptography;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Marca;

/// <summary>
/// Logo (marca visual) configurável do portal — linha ÚNICA por banco. É usada em dois escopos com a
/// MESMA forma: no CATÁLOGO guarda a logo GLOBAL (padrão do portal, exibida no login pré-autenticação);
/// no banco do TENANT guarda a logo do contribuinte (exibida quando ele é o contexto ativo).
///
/// Guarda apenas o binário da imagem, seu tipo e o hash de integridade — NUNCA segredo (regra 7). Aceita
/// somente formatos raster seguros (PNG/JPEG): SVG é recusado por ser vetor de XSS quando servido. O
/// tamanho é limitado (regra 10). O conteúdo é validado na fronteira; aqui a invariante é reafirmada.
/// </summary>
public sealed class MarcaVisual
{
    /// <summary>
    /// Tamanho máximo do arquivo de logo (10 MB) — cabe na coluna <c>mediumblob</c> (16 MB) e no limite de corpo da
    /// requisição (20 MB); barra contra abuso. O navegador guarda a logo em cache (ETag), então ela não é baixada a
    /// cada página.
    /// </summary>
    public const long TamanhoMaximoBytes = 10L * 1024 * 1024;

    /// <summary>O limite em texto, para as mensagens e a tela.</summary>
    public const string TamanhoMaximoTexto = "10 MB";

    /// <summary>Id fixo da linha única (por banco) — torna o upsert determinístico e idempotente (regra 12).</summary>
    public static readonly Guid IdUnico = Guid.Parse("00000000-0000-0000-0000-0000000dda01");

    private static readonly HashSet<string> TiposPermitidos =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg" };

    private MarcaVisual()
    {
    }

    private MarcaVisual(Guid id, byte[] conteudo, string contentType, string hashSha256, long tamanhoBytes, DateTimeOffset agoraUtc, Guid? atualizadoPor)
    {
        Id = id;
        Conteudo = conteudo;
        ContentType = contentType;
        HashSha256 = hashSha256;
        TamanhoBytes = tamanhoBytes;
        AtualizadoEmUtc = agoraUtc;
        AtualizadoPorUsuarioId = atualizadoPor;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    /// <summary>Bytes da imagem (PNG/JPEG). Nunca segredo.</summary>
    public byte[] Conteudo { get; private set; } = [];

    public string ContentType { get; private set; } = null!;

    /// <summary>Digest SHA-256 (64 hex minúsculos) — integridade e ETag de cache.</summary>
    public string HashSha256 { get; private set; } = null!;

    public long TamanhoBytes { get; private set; }

    public DateTimeOffset AtualizadoEmUtc { get; private set; }

    /// <summary>Quem atualizou (auditoria); nulo quando não aplicável.</summary>
    public Guid? AtualizadoPorUsuarioId { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    /// <summary>Cria a marca com a linha única, validando formato e tamanho (fronteira reafirmada).</summary>
    public static MarcaVisual Criar(byte[] conteudo, string contentType, DateTimeOffset agoraUtc, Guid? atualizadoPor)
    {
        var (validado, tipo, hash) = Validar(conteudo, contentType);
        return new MarcaVisual(IdUnico, validado, tipo, hash, validado.Length, agoraUtc, atualizadoPor);
    }

    /// <summary>Substitui o conteúdo da marca (mesma linha única) — operação idempotente por natureza.</summary>
    public void Atualizar(byte[] conteudo, string contentType, DateTimeOffset agoraUtc, Guid? atualizadoPor)
    {
        var (validado, tipo, hash) = Validar(conteudo, contentType);
        Conteudo = validado;
        ContentType = tipo;
        HashSha256 = hash;
        TamanhoBytes = validado.Length;
        AtualizadoEmUtc = agoraUtc;
        AtualizadoPorUsuarioId = atualizadoPor;
        ConcurrencyStamp = Guid.NewGuid();
    }

    private static (byte[] Conteudo, string ContentType, string Hash) Validar(byte[] conteudo, string contentType)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        if (string.IsNullOrWhiteSpace(contentType) || !TiposPermitidos.Contains(contentType))
        {
            throw new EntradaInvalidaException("Formato de logo não suportado. Use PNG ou JPEG.");
        }

        if (conteudo.Length == 0)
        {
            throw new EntradaInvalidaException("Arquivo de logo vazio.");
        }

        if (conteudo.Length > TamanhoMaximoBytes)
        {
            throw new EntradaInvalidaException($"Logo excede o tamanho máximo permitido ({TamanhoMaximoTexto}).");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(conteudo));
        return (conteudo, contentType.ToLowerInvariant(), hash);
    }
}
