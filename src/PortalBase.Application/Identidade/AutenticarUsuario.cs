using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Identidade;

/// <summary>Escopo pseudo-tenant usado em eventos de auditoria de identidade (não são por tenant).</summary>
public static class EscoposAuditoria
{
    public const string Identidade = "identidade";

    /// <summary>Operações GLOBAIS de plataforma (mesmo escopo usado pela autorização de plataforma).</summary>
    public const string Plataforma = "GLOBAL";
}

/// <summary>
/// Resultado da autenticação. Traz o mínimo para a apresentação emitir a sessão: id,
/// se o MFA é exigível (o login ainda NÃO satisfaz o segundo fator — seção 19) e a marca
/// de segurança que a sessão deve carregar para permitir revalidação/revogação (seção 28).
/// </summary>
/// <param name="AdministradorPlataforma">Perfil "Administrador da plataforma" (a sessão nasce com a dispensa do segundo fator).</param>
public sealed record ResultadoAutenticacao(
    Guid UsuarioId, string Login, string Nome, bool MfaHabilitado, Guid SecurityStamp, bool AdministradorPlataforma = false,
    bool CadastroMfaPendente = false, bool TrocaSenhaPendente = false);

/// <summary>
/// Caso de uso de LOGIN (seções 4, 7, 26). O usuário se identifica pelo E-MAIL (sem diferenciar maiúsculas de
/// minúsculas). Verifica a senha com hash vetado, aplica lockout por tentativas e responde de forma GENÉRICA (mesma
/// exceção) para e-mail desconhecido, senha incorreta ou conta bloqueada — sem permitir enumeração. Nunca registra a
/// senha nem o e-mail informado.
/// </summary>
public sealed class AutenticarUsuario
{
    private const string Operacao = "Login";

    private readonly IRepositorioUsuario _usuarios;
    private readonly IHasherSenha _hasher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly PoliticaAutenticacao _politica;

    public AutenticarUsuario(
        IRepositorioUsuario usuarios,
        IHasherSenha hasher,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio,
        PoliticaAutenticacao politica)
    {
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(politica);

        _usuarios = usuarios;
        _hasher = hasher;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
        _politica = politica;
    }

    /// <exception cref="CredenciaisInvalidasException">Sempre que a autenticação não for bem-sucedida.</exception>
    public async Task<ResultadoAutenticacao> ExecutarAsync(string? email, string? senha, CancellationToken cancellationToken = default)
    {
        var agora = _relogio.AgoraUtc;
        // Entrada hostil (regra 2): vazia ou maior que qualquer e-mail gravável nem chega ao banco — mesmo caminho genérico.
        var usuario = string.IsNullOrWhiteSpace(email) || email.Trim().Length > Usuario.TamanhoMaximoEmail
            ? null
            : await _usuarios.ObterPorEmailAsync(email, cancellationToken).ConfigureAwait(false);

        // E-mail desconhecido OU sem credencial OU impedido: caminho e resposta idênticos (anti-enumeração).
        if (usuario is null || !usuario.PossuiCredencial || usuario.EstaImpedidoDeAutenticar(agora))
        {
            await AuditarAsync(usuario?.Id.ToString() ?? "desconhecido", ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
            throw new CredenciaisInvalidasException();
        }

        var verificacao = _hasher.Verificar(usuario.HashSenha!, senha ?? string.Empty);
        if (verificacao == ResultadoVerificacaoSenha.Falha)
        {
            usuario.RegistrarFalhaAutenticacao(_politica.MaxTentativas, _politica.DuracaoBloqueio, agora);
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            await AuditarAsync(usuario.Id.ToString(), ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
            throw new CredenciaisInvalidasException();
        }

        if (verificacao == ResultadoVerificacaoSenha.SucessoPrecisaRehash)
        {
            usuario.DefinirCredencial(_hasher.Gerar(senha!), agora);
        }

        usuario.RegistrarSucessoAutenticacao(agora);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(usuario.Id.ToString(), ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

        return new ResultadoAutenticacao(
            usuario.Id, usuario.Login, usuario.Nome, usuario.MfaHabilitado, usuario.SecurityStamp, usuario.AdministradorPlataforma,
            usuario.CadastroMfaPendente, usuario.TrocarSenhaNoProximoAcesso);
    }

    private ValueTask AuditarAsync(string principal, ResultadoAuditoria resultado, CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(principal, EscoposAuditoria.Identidade, Operacao, resultado, _relogio.AgoraUtc),
            cancellationToken);
}
