using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Identidade;

public enum ResultadoAlteracaoSenha
{
    Alterada,

    /// <summary>A senha atual não confere (conta como tentativa: o mesmo bloqueio do login se aplica).</summary>
    SenhaAtualIncorreta,

    SenhasDiferentes,

    /// <summary>A nova senha é igual à atual.</summary>
    MesmaSenha,

    SenhaForaDaPolitica,
}

/// <summary>Resultado da troca: o novo security stamp, para a sessão ATUAL continuar (as demais são encerradas).</summary>
public sealed record AlteracaoSenha(ResultadoAlteracaoSenha Resultado, Guid? NovoSecurityStamp = null);

/// <summary>
/// Troca de senha do usuário LOGADO. O usuário vem do principal autenticado (resolvido no servidor, nunca do
/// formulário). Exige a senha atual — errar conta como tentativa de autenticação (lockout, como no login) —, valida
/// a nova pela <see cref="PoliticaSenha"/>, rotaciona o security stamp (encerra as outras sessões) e audita. Nunca
/// registra senha.
/// </summary>
public sealed class AlterarSenha
{
    private readonly IRepositorioUsuario _usuarios;
    private readonly IHasherSenha _hasher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly PoliticaAutenticacao _politica;

    public AlterarSenha(
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

    /// <exception cref="NaoAutenticadoException">Usuário inexistente, sem senha ou impedido (inativo/bloqueado).</exception>
    public async Task<AlteracaoSenha> ExecutarAsync(
        Guid usuarioId, string? senhaAtual, string? novaSenha, string? confirmacao, CancellationToken cancellationToken = default)
    {
        var agora = _relogio.AgoraUtc;
        var usuario = usuarioId == Guid.Empty ? null : await _usuarios.ObterPorIdAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        if (usuario is null || !usuario.PossuiCredencial || usuario.EstaImpedidoDeAutenticar(agora))
        {
            await AuditarAsync(usuarioId.ToString(), ResultadoAuditoria.Negado, "usuário inexistente, sem senha ou impedido", cancellationToken).ConfigureAwait(false);
            throw new NaoAutenticadoException();
        }

        if (_hasher.Verificar(usuario.HashSenha!, senhaAtual ?? string.Empty) == ResultadoVerificacaoSenha.Falha)
        {
            usuario.RegistrarFalhaAutenticacao(_politica.MaxTentativas, _politica.DuracaoBloqueio, agora);
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            await AuditarAsync(usuario.Id.ToString(), ResultadoAuditoria.Negado, "senha atual incorreta", cancellationToken).ConfigureAwait(false);
            return new AlteracaoSenha(ResultadoAlteracaoSenha.SenhaAtualIncorreta);
        }

        if (!string.Equals(novaSenha, confirmacao, StringComparison.Ordinal))
        {
            return new AlteracaoSenha(ResultadoAlteracaoSenha.SenhasDiferentes);
        }

        if (string.Equals(novaSenha, senhaAtual, StringComparison.Ordinal))
        {
            return new AlteracaoSenha(ResultadoAlteracaoSenha.MesmaSenha);
        }

        if (PoliticaSenha.Validar(novaSenha, usuario.Login, usuario.Email) is not null)
        {
            return new AlteracaoSenha(ResultadoAlteracaoSenha.SenhaForaDaPolitica);
        }

        usuario.DefinirCredencial(_hasher.Gerar(novaSenha!), agora);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(usuario.Id.ToString(), ResultadoAuditoria.Permitido, "senha trocada pelo próprio usuário; outras sessões encerradas", cancellationToken).ConfigureAwait(false);
        return new AlteracaoSenha(ResultadoAlteracaoSenha.Alterada, usuario.SecurityStamp);
    }

    private ValueTask AuditarAsync(string principal, ResultadoAuditoria resultado, string motivo, CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(principal, EscoposAuditoria.Identidade, AcaoAuditoria.PasswordChanged, resultado, _relogio.AgoraUtc)
            {
                TipoRecurso = "Usuario",
                RecursoId = principal,
                Motivo = motivo,
            },
            cancellationToken);
}
