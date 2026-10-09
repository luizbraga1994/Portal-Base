using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Identidade;

/// <summary>Resultado do comando do primeiro administrador (mensagem segura para exibir — nunca ecoa a senha).</summary>
public sealed record ResultadoPrimeiroAdministrador(bool Sucesso, string Mensagem, Guid? UsuarioId = null);

/// <summary>
/// PRIMEIRO ADMINISTRADOR DA PLATAFORMA de uma implantação nova (catálogo sem nenhum). Executado NO SERVIDOR, por quem
/// administra a máquina (comando <c>PortalBase.Web.exe criar-primeiro-administrador</c>) — não há tela nem endpoint.
/// Fail-closed: só funciona enquanto NÃO existe nenhum Administrador da plataforma; depois, novos administradores só
/// pela tela, concedidos por outro administrador. Senha pela política do portal, guardada só como hash; auditado.
/// </summary>
public sealed class PrimeiroAdministradorPlataforma
{
    private readonly IRepositorioUsuario _usuarios;
    private readonly IHasherSenha _hasher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public PrimeiroAdministradorPlataforma(
        IRepositorioUsuario usuarios, IHasherSenha hasher, IUnitOfWork unitOfWork, IAuditoria auditoria, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        _usuarios = usuarios;
        _hasher = hasher;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    /// <summary>Já existe administrador? (o comando avisa antes de pedir os dados).</summary>
    public Task<bool> JaExisteAsync(CancellationToken cancellationToken = default) => _usuarios.ExisteAdministradorPlataformaAsync(cancellationToken);

    /// <param name="executor">Quem rodou o comando no servidor (conta do Windows) — só para a auditoria.</param>
    public async Task<ResultadoPrimeiroAdministrador> CriarAsync(
        string? nome, string? email, string? senha, string executor, CancellationToken cancellationToken = default)
    {
        if (await _usuarios.ExisteAdministradorPlataformaAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(false, "Já existe Administrador da plataforma. Novos administradores são concedidos pela tela (Administração → Usuários).");
        }

        var nomeLimpo = nome?.Trim() ?? string.Empty;
        if (nomeLimpo.Length == 0)
        {
            return new(false, "Informe o nome.");
        }

        Usuario usuario;
        var agora = _relogio.AgoraUtc;
        try
        {
            var emailNormalizado = Usuario.NormalizarEmail(email ?? string.Empty);
            usuario = Usuario.Criar(emailNormalizado, nomeLimpo, emailNormalizado, agora);
        }
        catch (EntradaInvalidaException)
        {
            return new(false, "Informe um e-mail válido (ele é o login) e um nome de até 200 caracteres.");
        }

        if (await _usuarios.ObterPorEmailAsync(usuario.Email, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new(false, "Já existe um usuário com este e-mail. Use outro e-mail.");
        }

        var motivo = PoliticaSenha.Validar(senha, usuario.Login, usuario.Email);
        if (motivo is not null)
        {
            return new(false, motivo);
        }

        usuario.DefinirCredencial(_hasher.Gerar(senha!), agora);
        usuario.DefinirAdministradorPlataforma(true, agora);
        await _usuarios.AdicionarAsync(usuario, cancellationToken).ConfigureAwait(false);
        try
        {
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            return new(false, "Já existe um usuário com este e-mail. Use outro e-mail.");
        }

        var ator = $"servidor:{(string.IsNullOrWhiteSpace(executor) ? "desconhecido" : executor.Trim())}";
        await _auditoria.RegistrarAsync(
            new EventoAuditoria(ator, EscoposAuditoria.Identidade, AcaoAuditoria.UserCreated, ResultadoAuditoria.Permitido, agora)
            {
                TipoRecurso = "Usuario",
                RecursoId = usuario.Id.ToString(),
                Motivo = "Primeiro administrador da plataforma (comando no servidor)",
            },
            cancellationToken).ConfigureAwait(false);
        await _auditoria.RegistrarAsync(
            new EventoAuditoria(ator, EscoposAuditoria.Plataforma, AcaoAuditoria.PlatformAdminGranted, ResultadoAuditoria.Permitido, agora)
            {
                TipoRecurso = "Usuario",
                RecursoId = usuario.Id.ToString(),
                Motivo = "Primeiro administrador da plataforma (comando no servidor)",
            },
            cancellationToken).ConfigureAwait(false);

        return new(true, "Administrador da plataforma criado. Entre no portal com este e-mail e a senha definida.", usuario.Id);
    }
}
