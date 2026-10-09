using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Token de recuperação de conta (seção 27). Persistido <b>somente como hash</b> (nunca o
/// valor em claro), com expiração e uso único. O valor real é entregue ao usuário por canal
/// seguro e nunca registrado em log.
/// </summary>
public sealed class TokenRecuperacaoConta
{
    private const int LimiteHash = 512;

    private TokenRecuperacaoConta()
    {
    }

    private TokenRecuperacaoConta(Guid id, Guid usuarioId, string hashToken, DateTimeOffset criadoUtc, DateTimeOffset expiraUtc)
    {
        Id = id;
        UsuarioId = usuarioId;
        HashToken = hashToken;
        CriadoEmUtc = criadoUtc;
        ExpiraEmUtc = expiraUtc;
    }

    public Guid Id { get; private set; }

    public Guid UsuarioId { get; private set; }

    /// <summary>Hash do token (nunca o token em claro).</summary>
    public string HashToken { get; private set; } = null!;

    public DateTimeOffset CriadoEmUtc { get; private set; }

    public DateTimeOffset ExpiraEmUtc { get; private set; }

    public DateTimeOffset? UsadoEmUtc { get; private set; }

    public static TokenRecuperacaoConta Emitir(Guid usuarioId, string hashToken, DateTimeOffset agoraUtc, TimeSpan validade)
    {
        if (usuarioId == Guid.Empty)
        {
            throw new EntradaInvalidaException("Usuário é obrigatório.");
        }

        if (validade <= TimeSpan.Zero)
        {
            throw new EntradaInvalidaException("Validade do token deve ser positiva.");
        }

        return new TokenRecuperacaoConta(
            Guid.NewGuid(),
            usuarioId,
            Exigir.TextoObrigatorioComLimite(hashToken, LimiteHash, nameof(HashToken)),
            agoraUtc,
            agoraUtc.Add(validade));
    }

    public bool EstaValido(DateTimeOffset agoraUtc) => UsadoEmUtc is null && ExpiraEmUtc > agoraUtc;

    /// <summary>Consome o token (uso único). Falha se expirado ou já usado.</summary>
    public void Consumir(DateTimeOffset agoraUtc)
    {
        if (!EstaValido(agoraUtc))
        {
            throw new EstadoInvalidoException("Token de recuperação inválido ou expirado.");
        }

        UsadoEmUtc = agoraUtc;
    }
}

/// <summary>
/// Código de recuperação de MFA (seção 36). Gerado em lote na ativação do MFA; persistido
/// <b>somente como hash</b>, com uso único. Permite recuperação controlada sem expor o
/// segredo TOTP. Nunca registrado em log.
/// </summary>
public sealed class CodigoRecuperacaoMfa
{
    private const int LimiteHash = 512;

    private CodigoRecuperacaoMfa()
    {
    }

    private CodigoRecuperacaoMfa(Guid id, Guid usuarioId, string hashCodigo, DateTimeOffset criadoUtc)
    {
        Id = id;
        UsuarioId = usuarioId;
        HashCodigo = hashCodigo;
        CriadoEmUtc = criadoUtc;
    }

    public Guid Id { get; private set; }

    public Guid UsuarioId { get; private set; }

    /// <summary>Hash do código (nunca o código em claro).</summary>
    public string HashCodigo { get; private set; } = null!;

    public DateTimeOffset CriadoEmUtc { get; private set; }

    public DateTimeOffset? UsadoEmUtc { get; private set; }

    public bool Disponivel => UsadoEmUtc is null;

    public static CodigoRecuperacaoMfa Gerar(Guid usuarioId, string hashCodigo, DateTimeOffset agoraUtc)
    {
        if (usuarioId == Guid.Empty)
        {
            throw new EntradaInvalidaException("Usuário é obrigatório.");
        }

        return new CodigoRecuperacaoMfa(
            Guid.NewGuid(),
            usuarioId,
            Exigir.TextoObrigatorioComLimite(hashCodigo, LimiteHash, nameof(HashCodigo)),
            agoraUtc);
    }

    public void Consumir(DateTimeOffset agoraUtc)
    {
        if (!Disponivel)
        {
            throw new EstadoInvalidoException("Código de recuperação já utilizado.");
        }

        UsadoEmUtc = agoraUtc;
    }
}
