using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Usuário central da plataforma. Agregado encapsulado (sem setters públicos; alterações
/// por métodos controlados — seção 21). Guarda apenas <b>estado</b> de identidade: o hash
/// da senha (nunca a senha), o segredo MFA <b>já protegido</b> pela infraestrutura (nunca
/// em claro), contadores de bloqueio e um <c>SecurityStamp</c> para revalidação/revogação
/// de sessão (seção 28). Nenhuma primitiva criptográfica vive no domínio.
/// </summary>
public sealed class Usuario
{
    /// <summary>Tamanho máximo do e-mail (o identificador do login).</summary>
    public const int TamanhoMaximoEmail = 256;

    private const int LimiteLogin = 128;
    private const int LimiteNome = 200;
    private const int LimiteHash = 512;
    private const int LimiteSegredoMfa = 1024;

    private Usuario()
    {
    }

    private Usuario(Guid id, string login, string nome, string email, DateTimeOffset agoraUtc)
    {
        Id = id;
        Login = login;
        Nome = nome;
        Email = email;
        Status = StatusUsuario.Ativo;
        CriadoEmUtc = agoraUtc;
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
        SecurityStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    public string Login { get; private set; } = null!;

    public string Nome { get; private set; } = null!;

    public string Email { get; private set; } = null!;

    public StatusUsuario Status { get; private set; }

    /// <summary>Hash da senha (formato versionado do hasher). Nulo até definir credencial. Nunca a senha.</summary>
    public string? HashSenha { get; private set; }

    /// <summary>Segredo TOTP <b>já protegido</b> pela infraestrutura (nunca em claro). Nulo se MFA não iniciado.</summary>
    public string? SegredoMfaProtegido { get; private set; }

    /// <summary>
    /// Segredo TOTP NOVO (já protegido) de um RECADASTRO em andamento (outro celular). Enquanto não confirmado, o
    /// segredo atual continua valendo — abandonar o recadastro nunca deixa a conta sem segundo fator.
    /// </summary>
    public string? SegredoMfaPendenteProtegido { get; private set; }

    /// <summary>MFA confirmado e ativo (segundo fator exigível). Enquanto falso, o segredo é apenas pendente.</summary>
    public bool MfaHabilitado { get; private set; }

    public DateTimeOffset? MfaConfirmadoEmUtc { get; private set; }

    /// <summary>Tentativas de autenticação malsucedidas consecutivas (zera no sucesso).</summary>
    public int AcessosFalhosConsecutivos { get; private set; }

    /// <summary>Bloqueio temporário por tentativas (lockout). Nulo quando não bloqueado por tentativas.</summary>
    public DateTimeOffset? BloqueadoAteUtc { get; private set; }

    public DateTimeOffset CriadoEmUtc { get; private set; }

    public DateTimeOffset AtualizadoEmUtc { get; private set; }

    public DateTimeOffset? UltimoAcessoUtc { get; private set; }

    /// <summary>Concorrência otimista (persistência).</summary>
    public Guid ConcurrencyStamp { get; private set; }

    /// <summary>
    /// Marca de segurança: rotacionada em toda mudança sensível (credencial, MFA, status,
    /// revogação). A revalidação de sessão compara o carimbo do cookie com este; divergência
    /// invalida a sessão (revogação quase-imediata em Blazor Server — seção 28).
    /// </summary>
    public Guid SecurityStamp { get; private set; }

    /// <summary>
    /// Perfil "Administrador da plataforma": acesso TOTAL — todas as permissões de plataforma e todas as permissões em
    /// QUALQUER contribuinte, sem exigir o segundo fator. Global (não é papel de um contribuinte); concedido e removido
    /// só por outro administrador da plataforma. A decisão continua no servidor (autorização/catálogo).
    /// </summary>
    public bool AdministradorPlataforma { get; private set; }

    /// <summary>
    /// MFA EXIGIDO pelo administrador: enquanto o autenticador não estiver cadastrado, o login leva o usuário ao cadastro
    /// do MFA (QR code) antes de qualquer outra tela.
    /// </summary>
    public bool MfaObrigatorio { get; private set; }

    /// <summary>
    /// A senha atual é TEMPORÁRIA (gerada pelo portal e enviada por e-mail): no próximo acesso o usuário é levado a
    /// trocá-la antes de qualquer outra tela. Limpa ao definir uma senha nova.
    /// </summary>
    public bool TrocarSenhaNoProximoAcesso { get; private set; }

    /// <summary>O login precisa passar pelo cadastro do MFA (exigido e ainda não cadastrado)?</summary>
    public bool CadastroMfaPendente => MfaObrigatorio && !MfaHabilitado;

    public bool PossuiCredencial => !string.IsNullOrEmpty(HashSenha);

    public static Usuario Criar(string login, string nome, string email, DateTimeOffset agoraUtc, Guid? id = null)
    {
        var emailNormalizado = NormalizarEmail(Exigir.TextoObrigatorioComLimite(email, TamanhoMaximoEmail, nameof(Email)));
        if (!EmailPareceValido(emailNormalizado))
        {
            throw new EntradaInvalidaException("Email inválido.");
        }

        return new Usuario(
            id ?? Guid.NewGuid(),
            Exigir.TextoObrigatorioComLimite(login, LimiteLogin, nameof(Login)),
            Exigir.TextoObrigatorioComLimite(nome, LimiteNome, nameof(Nome)),
            emailNormalizado,
            agoraUtc);
    }

    /// <summary>
    /// Forma canônica do e-mail, que é o identificador do LOGIN: sem espaços nas pontas e em minúsculas. Assim
    /// "Admin@Portal.com" e "admin@portal.com" são o MESMO usuário — o catálogo compara texto com collation sensível a
    /// caixa, então gravação e busca precisam usar esta mesma forma.
    /// </summary>
    public static string NormalizarEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>Define/atualiza o hash da senha. Rotaciona o <c>SecurityStamp</c> e limpa o lockout.</summary>
    public void DefinirCredencial(string hashSenha, DateTimeOffset agoraUtc)
    {
        HashSenha = Exigir.TextoObrigatorioComLimite(hashSenha, LimiteHash, nameof(HashSenha));
        TrocarSenhaNoProximoAcesso = false;
        AcessosFalhosConsecutivos = 0;
        BloqueadoAteUtc = null;
        Rotacionar(agoraUtc);
    }

    /// <summary>
    /// Define uma senha TEMPORÁRIA (gerada pelo portal e enviada por e-mail): como <see cref="DefinirCredencial"/>, e o
    /// próximo acesso exige a troca. As sessões abertas caem (marca de segurança).
    /// </summary>
    public void DefinirSenhaTemporaria(string hashSenha, DateTimeOffset agoraUtc)
    {
        DefinirCredencial(hashSenha, agoraUtc);
        TrocarSenhaNoProximoAcesso = true;
    }

    /// <summary>Liga/desliga a exigência de MFA (o cadastro acontece no próximo login do usuário). Idempotente.</summary>
    public void DefinirMfaObrigatorio(bool obrigatorio, DateTimeOffset agoraUtc)
    {
        if (MfaObrigatorio == obrigatorio)
        {
            return;
        }

        MfaObrigatorio = obrigatorio;
        ConcurrencyStamp = Guid.NewGuid();
        AtualizadoEmUtc = agoraUtc;
    }

    /// <summary>Está impedido de autenticar agora (inativo/bloqueado ou lockout temporário vigente)?</summary>
    public bool EstaImpedidoDeAutenticar(DateTimeOffset agoraUtc)
        => Status != StatusUsuario.Ativo
            || (BloqueadoAteUtc is { } ate && ate > agoraUtc);

    /// <summary>Registra falha de autenticação; ao atingir o limite, aplica lockout temporário.</summary>
    public void RegistrarFalhaAutenticacao(int limiteTentativas, TimeSpan duracaoBloqueio, DateTimeOffset agoraUtc)
    {
        if (limiteTentativas < 1)
        {
            throw new EntradaInvalidaException("Limite de tentativas deve ser positivo.");
        }

        AcessosFalhosConsecutivos++;
        if (AcessosFalhosConsecutivos >= limiteTentativas)
        {
            BloqueadoAteUtc = agoraUtc.Add(duracaoBloqueio);
            AcessosFalhosConsecutivos = 0;
        }

        AtualizadoEmUtc = agoraUtc;
    }

    /// <summary>Registra autenticação bem-sucedida: zera contadores e marca o último acesso.</summary>
    public void RegistrarSucessoAutenticacao(DateTimeOffset agoraUtc)
    {
        AcessosFalhosConsecutivos = 0;
        BloqueadoAteUtc = null;
        UltimoAcessoUtc = agoraUtc;
        AtualizadoEmUtc = agoraUtc;
    }

    /// <summary>
    /// Prepara a ativação do MFA guardando o segredo já protegido (pendente de confirmação). Com MFA JÁ ativo
    /// (recadastro), o novo segredo fica em <see cref="SegredoMfaPendenteProtegido"/> e o atual continua valendo até a
    /// confirmação — nunca há janela sem segundo fator.
    /// </summary>
    public void PrepararMfa(string segredoProtegido, DateTimeOffset agoraUtc)
    {
        var segredo = Exigir.TextoObrigatorioComLimite(segredoProtegido, LimiteSegredoMfa, nameof(SegredoMfaProtegido));
        if (MfaHabilitado && !string.IsNullOrEmpty(SegredoMfaProtegido))
        {
            SegredoMfaPendenteProtegido = segredo;
            AtualizadoEmUtc = agoraUtc;
            return;
        }

        SegredoMfaProtegido = segredo;
        SegredoMfaPendenteProtegido = null;
        MfaHabilitado = false;
        MfaConfirmadoEmUtc = null;
        AtualizadoEmUtc = agoraUtc;
    }

    /// <summary>Segredo a conferir na confirmação: o do recadastro, se houver; senão o da primeira ativação.</summary>
    public string? SegredoMfaEmConfirmacao => SegredoMfaPendenteProtegido ?? SegredoMfaProtegido;

    /// <summary>Confirma o MFA (após validar um código). Torna o segundo fator exigível; no recadastro, troca o segredo.</summary>
    public void ConfirmarMfa(DateTimeOffset agoraUtc)
    {
        if (!string.IsNullOrEmpty(SegredoMfaPendenteProtegido))
        {
            SegredoMfaProtegido = SegredoMfaPendenteProtegido;
            SegredoMfaPendenteProtegido = null;
        }
        else if (string.IsNullOrEmpty(SegredoMfaProtegido))
        {
            throw new EstadoInvalidoException("MFA não iniciado.");
        }

        MfaHabilitado = true;
        MfaConfirmadoEmUtc = agoraUtc;
        Rotacionar(agoraUtc);
    }

    /// <summary>Desabilita o MFA (recuperação controlada/administração). Rotaciona a marca de segurança.</summary>
    public void DesabilitarMfa(DateTimeOffset agoraUtc)
    {
        SegredoMfaProtegido = null;
        SegredoMfaPendenteProtegido = null;
        MfaHabilitado = false;
        MfaConfirmadoEmUtc = null;
        Rotacionar(agoraUtc);
    }

    /// <summary>Invalida sessões existentes rotacionando a marca de segurança (revogação — seção 28).</summary>
    public void RevogarSessoes(DateTimeOffset agoraUtc) => Rotacionar(agoraUtc);

    /// <summary>
    /// Concede ou remove o perfil "Administrador da plataforma". Mudar rotaciona o <c>SecurityStamp</c>: a sessão
    /// vigente cai e a próxima já nasce com (ou sem) a dispensa do segundo fator.
    /// </summary>
    public void DefinirAdministradorPlataforma(bool administrador, DateTimeOffset agoraUtc)
    {
        if (AdministradorPlataforma == administrador)
        {
            return;
        }

        AdministradorPlataforma = administrador;
        Rotacionar(agoraUtc);
    }

    /// <summary>
    /// Altera nome e e-mail (o e-mail é o login, sempre na forma canônica). Trocar o e-mail rotaciona o
    /// <c>SecurityStamp</c> (sessões abertas caem); só o nome não derruba sessões.
    /// </summary>
    public void AlterarDados(string nome, string email, DateTimeOffset agoraUtc)
    {
        var nomeValido = Exigir.TextoObrigatorioComLimite(nome, LimiteNome, nameof(Nome));
        var emailNormalizado = NormalizarEmail(Exigir.TextoObrigatorioComLimite(email, TamanhoMaximoEmail, nameof(Email)));
        if (!EmailPareceValido(emailNormalizado))
        {
            throw new EntradaInvalidaException("Email inválido.");
        }

        var emailMudou = !string.Equals(Email, emailNormalizado, StringComparison.Ordinal);
        if (!emailMudou && string.Equals(Nome, nomeValido, StringComparison.Ordinal))
        {
            return;
        }

        Nome = nomeValido;
        Email = emailNormalizado;
        if (emailMudou)
        {
            Rotacionar(agoraUtc);
        }
        else
        {
            ConcurrencyStamp = Guid.NewGuid();
            AtualizadoEmUtc = agoraUtc;
        }
    }

    /// <summary>Libera o bloqueio por tentativas (e o status Bloqueado), sem mexer na senha.</summary>
    public void Desbloquear(DateTimeOffset agoraUtc)
    {
        AcessosFalhosConsecutivos = 0;
        BloqueadoAteUtc = null;
        if (Status == StatusUsuario.Bloqueado)
        {
            DefinirStatus(StatusUsuario.Ativo, agoraUtc);
            return;
        }

        ConcurrencyStamp = Guid.NewGuid();
        AtualizadoEmUtc = agoraUtc;
    }

    public void RegistrarAcesso(DateTimeOffset agoraUtc) => UltimoAcessoUtc = agoraUtc;

    public void Ativar(DateTimeOffset agoraUtc) => DefinirStatus(StatusUsuario.Ativo, agoraUtc);

    public void Inativar(DateTimeOffset agoraUtc) => DefinirStatus(StatusUsuario.Inativo, agoraUtc);

    public void Bloquear(DateTimeOffset agoraUtc) => DefinirStatus(StatusUsuario.Bloqueado, agoraUtc);

    private void DefinirStatus(StatusUsuario status, DateTimeOffset agoraUtc)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        Rotacionar(agoraUtc); // status inativo/bloqueado deve invalidar sessões vigentes
    }

    private void Rotacionar(DateTimeOffset agoraUtc)
    {
        SecurityStamp = Guid.NewGuid();
        ConcurrencyStamp = Guid.NewGuid();
        AtualizadoEmUtc = agoraUtc;
    }

    private static bool EmailPareceValido(string email)
    {
        var arroba = email.IndexOf('@', StringComparison.Ordinal);
        return arroba > 0
            && arroba < email.Length - 1
            && email.IndexOf('@', arroba + 1) < 0
            && email.IndexOf('.', arroba + 1) > 0
            && !email.Contains(' ', StringComparison.Ordinal);
    }
}
