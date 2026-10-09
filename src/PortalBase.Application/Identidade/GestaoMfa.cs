using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Identidade;

/// <summary>Segredo TOTP para provisionar o autenticador (mostrado uma única vez ao usuário).</summary>
public sealed record SegredoMfaProvisionamento(string SegredoBase32, string UriProvisionamento);

/// <summary>Ativação de MFA concluída: entrega os códigos de recuperação em claro UMA vez.</summary>
public sealed record AtivacaoMfaConcluida(IReadOnlyList<string> CodigosRecuperacao);

/// <summary>
/// Ativação, confirmação e validação (step-up) do segundo fator pelo PRÓPRIO usuário (seções 18, 19,
/// 36). O segredo TOTP é guardado SEMPRE protegido (nunca em claro); códigos e segredos nunca
/// aparecem em log nem em mensagens de erro. Auditoria de sucesso/falha.
/// </summary>
public sealed class GestaoMfa
{
    private const int QuantidadeCodigosRecuperacao = 8;
    private const string Emissor = "Portal Base";

    private readonly IRepositorioUsuario _usuarios;
    private readonly IProvedorTotp _totp;
    private readonly IProtetorSegredoMfa _protetor;
    private readonly IGeradorTokens _geradorTokens;
    private readonly IHashDeterministico _hash;
    private readonly IRegistroConsumoTotp _consumoTotp;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public GestaoMfa(
        IRepositorioUsuario usuarios,
        IProvedorTotp totp,
        IProtetorSegredoMfa protetor,
        IGeradorTokens geradorTokens,
        IHashDeterministico hash,
        IRegistroConsumoTotp consumoTotp,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(totp);
        ArgumentNullException.ThrowIfNull(protetor);
        ArgumentNullException.ThrowIfNull(geradorTokens);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(consumoTotp);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _usuarios = usuarios;
        _totp = totp;
        _protetor = protetor;
        _geradorTokens = geradorTokens;
        _hash = hash;
        _consumoTotp = consumoTotp;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    /// <summary>Inicia a ativação: gera o segredo, guarda-o protegido (pendente) e devolve o URI/QR.</summary>
    public async Task<SegredoMfaProvisionamento> IniciarAtivacaoAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var usuario = await ExigirUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);

        var segredo = _totp.GerarSegredo();
        var protegido = await _protetor.ProtegerAsync(segredo, cancellationToken).ConfigureAwait(false);
        usuario.PrepararMfa(protegido, _relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(usuarioId, "MfaAtivacaoIniciada", ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

        var uri = _totp.GerarUriProvisionamento(segredo, Emissor, usuario.Login);
        return new SegredoMfaProvisionamento(segredo, uri);
    }

    /// <summary>Confirma a ativação validando um código do autenticador e gera os códigos de recuperação.</summary>
    public async Task<AtivacaoMfaConcluida> ConfirmarAtivacaoAsync(Guid usuarioId, string codigo, CancellationToken cancellationToken = default)
    {
        var usuario = await ExigirUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        var emConfirmacao = usuario.SegredoMfaEmConfirmacao; // recadastro: o NOVO segredo; o atual segue valendo até aqui
        if (string.IsNullOrEmpty(emConfirmacao))
        {
            throw new MfaInvalidoException();
        }

        var segredo = await _protetor.DesprotegerAsync(emConfirmacao, cancellationToken).ConfigureAwait(false);
        if (!_totp.Validar(segredo, codigo ?? string.Empty, _relogio.AgoraUtc))
        {
            await AuditarAsync(usuarioId, "MfaAtivacaoConfirmada", ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
            throw new MfaInvalidoException();
        }

        usuario.ConfirmarMfa(_relogio.AgoraUtc);

        var codigosClaros = new List<string>(QuantidadeCodigosRecuperacao);
        var entidades = new List<CodigoRecuperacaoMfa>(QuantidadeCodigosRecuperacao);
        for (var i = 0; i < QuantidadeCodigosRecuperacao; i++)
        {
            var codigoClaro = _geradorTokens.GerarCodigoRecuperacao();
            codigosClaros.Add(codigoClaro);
            var hashCodigo = await _hash.HashAsync(Normalizar(codigoClaro), cancellationToken).ConfigureAwait(false);
            entidades.Add(CodigoRecuperacaoMfa.Gerar(usuarioId, hashCodigo, _relogio.AgoraUtc));
        }

        // Recadastro (outro celular): os códigos de recuperação antigos deixam de valer.
        await _usuarios.RemoverCodigosRecuperacaoMfaAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        await _usuarios.AdicionarCodigosRecuperacaoMfaAsync(entidades, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(usuarioId, "MfaAtivacaoConfirmada", ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

        return new AtivacaoMfaConcluida(codigosClaros);
    }

    /// <summary>Valida o segundo fator (step-up): aceita um código TOTP OU um código de recuperação (uso único).</summary>
    /// <exception cref="MfaInvalidoException">Código inválido/expirado ou MFA não habilitado.</exception>
    public async Task ValidarAsync(Guid usuarioId, string codigo, CancellationToken cancellationToken = default)
    {
        var usuario = await ExigirUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        if (!usuario.MfaHabilitado || string.IsNullOrEmpty(usuario.SegredoMfaProtegido))
        {
            throw new MfaInvalidoException();
        }

        var segredo = await _protetor.DesprotegerAsync(usuario.SegredoMfaProtegido, cancellationToken).ConfigureAwait(false);
        var passo = _totp.ValidarObtendoPasso(segredo, codigo ?? string.Empty, _relogio.AgoraUtc);
        if (passo is { } passoValido)
        {
            // ANTI-REPLAY (Prompt 68): um código só vale UMA vez. Se o passo já foi consumido (código reapresentado
            // dentro da janela), o step-up é NEGADO — nunca aceita o mesmo código duas vezes.
            if (!_consumoTotp.TentarConsumir(usuarioId, passoValido, _relogio.AgoraUtc))
            {
                await AuditarAsync(usuarioId, "MfaValidadoReplay", ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
                throw new MfaInvalidoException();
            }

            await AuditarAsync(usuarioId, "MfaValidado", ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Tenta código de recuperação (uso único).
        if (!string.IsNullOrWhiteSpace(codigo))
        {
            var hashCodigo = await _hash.HashAsync(Normalizar(codigo), cancellationToken).ConfigureAwait(false);
            var recuperacao = await _usuarios.ObterCodigoRecuperacaoMfaAsync(usuarioId, hashCodigo, cancellationToken).ConfigureAwait(false);
            if (recuperacao is { Disponivel: true })
            {
                recuperacao.Consumir(_relogio.AgoraUtc);
                await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
                await AuditarAsync(usuarioId, "MfaValidadoRecuperacao", ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await AuditarAsync(usuarioId, "MfaValidado", ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
        throw new MfaInvalidoException();
    }

    // P74-R1 (GAP-P74R1-01): não há desativação/reset de MFA aqui. É ciclo de vida da identidade GLOBAL
    // (derruba o segundo fator em todos os contribuintes do usuário) e só pode existir como caso de uso de
    // PLATAFORMA, com permissão própria, step-up de MFA e auditoria do operador — decisão de produto pendente
    // (docs/ADR-IDENTIDADE-GLOBAL-VS-ACESSO-TENANT.md §5). Guardado por GuardaIdentidadeGlobalTests.

    private async Task<Usuario> ExigirUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var usuario = await _usuarios.ObterPorIdAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        return usuario ?? throw new NaoAutenticadoException();
    }

    // Normaliza o código de recuperação para comparação estável (sem separadores/caixa).
    private static string Normalizar(string codigo)
        => codigo.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private ValueTask AuditarAsync(Guid usuarioId, string operacao, ResultadoAuditoria resultado, CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(usuarioId.ToString(), EscoposAuditoria.Identidade, operacao, resultado, _relogio.AgoraUtc),
            cancellationToken);
}
