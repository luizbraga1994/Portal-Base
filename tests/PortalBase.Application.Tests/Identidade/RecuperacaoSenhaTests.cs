using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Identidade;
using PortalBase.Application.Notificacoes;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>"Esqueci a senha": anti-enumeração, link de uso único guardado só como hash, limite por hora, política,
/// lockout limpo, sessões revogadas e auditoria sem segredo.</summary>
public sealed class RecuperacaoSenhaTests
{
    private const string SenhaAntiga = "Senha-Antiga#2026";
    private const string SenhaNova = "Senha-Nova#2026x";
    private static readonly DateTimeOffset Inicio = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly FilaEmailFake _fila = new();
    private readonly AuditoriaFake _auditoria = new();
    private readonly RelogioAjustavel _relogio = new(Inicio);
    private readonly RecuperacaoSenha _recuperacao;

    public RecuperacaoSenhaTests()
        => _recuperacao = new RecuperacaoSenha(
            _usuarios, new GeradorTokensFake(), new HashDeterministicoFake(), new HasherSenhaFake(), _fila,
            new UnitOfWorkFake(), _auditoria, _relogio,
            new OpcoesRecuperacaoSenha("https://portal.exemplo.com.br/", TimeSpan.FromMinutes(30), 3));

    private Usuario Usuario(StatusUsuario status = StatusUsuario.Ativo)
    {
        var usuario = Domain.Catalogo.Usuario.Criar("maria", "Maria Silva", "maria@exemplo.com", Inicio);
        usuario.DefinirCredencial("H:" + SenhaAntiga, Inicio);
        if (status == StatusUsuario.Inativo)
        {
            usuario.Inativar(Inicio);
        }

        _usuarios.Usuarios.Add(usuario);
        return usuario;
    }

    [Fact]
    public async Task Com_Email_Desligado_O_Recurso_Fica_Indisponivel()
    {
        Usuario();
        _fila.Habilitado = false;

        (await _recuperacao.SolicitarAsync("maria@exemplo.com")).Should().Be(ResultadoSolicitacaoRecuperacao.Indisponivel);
        _usuarios.Tokens.Should().BeEmpty();
    }

    [Theory] // a resposta é a mesma: o portal nunca revela quem tem conta
    [InlineData("ninguem@exemplo.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Email_Desconhecido_Responde_Igual_E_Nada_E_Enviado(string? email)
    {
        Usuario();

        (await _recuperacao.SolicitarAsync(email)).Should().Be(ResultadoSolicitacaoRecuperacao.Aceita);

        _usuarios.Tokens.Should().BeEmpty();
        _fila.Mensagens.Should().BeEmpty();
        _auditoria.Eventos.Should().ContainSingle(e => e.Operacao == AcaoAuditoria.PasswordRecoveryStarted && e.Resultado == ResultadoAuditoria.Negado);
    }

    [Fact]
    public async Task Usuario_Inativo_Responde_Igual_E_Nada_E_Enviado()
    {
        Usuario(StatusUsuario.Inativo);

        (await _recuperacao.SolicitarAsync("maria@exemplo.com")).Should().Be(ResultadoSolicitacaoRecuperacao.Aceita);

        _fila.Mensagens.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_Cadastrado_Gera_Link_Guardado_So_Como_Hash_E_Envia_Ao_Email_Do_Cadastro()
    {
        var usuario = Usuario();

        (await _recuperacao.SolicitarAsync("  MARIA@Exemplo.com ")).Should().Be(ResultadoSolicitacaoRecuperacao.Aceita);

        var token = _usuarios.Tokens.Should().ContainSingle().Subject;
        token.HashToken.Should().Be("D:token-0", "só o HASH do código vai para o banco");
        token.ExpiraEmUtc.Should().Be(Inicio.AddMinutes(30));
        var email = _fila.Mensagens.Should().ContainSingle().Subject;
        email.Para.Should().Be("maria@exemplo.com");
        email.Texto.Should().Contain("https://portal.exemplo.com.br/conta/redefinir-senha?token=token-0").And.Contain("30 minutos");
        email.Html.Should().Contain("https://portal.exemplo.com.br/conta/redefinir-senha?token=token-0");
        _auditoria.Eventos.Should().ContainSingle(e => e.Operacao == AcaoAuditoria.PasswordRecoveryStarted && e.Resultado == ResultadoAuditoria.Permitido && e.PrincipalId == usuario.Id.ToString());
        _auditoria.Eventos.Should().NotContain(e => (e.Motivo ?? string.Empty).Contains("token-0"), "o código nunca vai para a auditoria");
    }

    [Fact] // bug de 08/10/2026: o link saiu relativo ("/conta/redefinir-senha?…") e não abria no leitor de e-mail
    public async Task O_Link_Usa_O_Endereco_Vigente_Na_Hora_Do_Envio_E_Nunca_Sai_Relativo()
    {
        var usuario = Usuario();
        var endereco = string.Empty; // a tela de e-mail ainda não tinha o endereço quando o circuito começou
        var recuperacao = new RecuperacaoSenha(
            _usuarios, new GeradorTokensFake(), new HashDeterministicoFake(), new HasherSenhaFake(), _fila,
            new UnitOfWorkFake(), _auditoria, _relogio, OpcoesRecuperacaoSenha.Vigente(() => endereco));

        recuperacao.Disponivel.Should().BeFalse("sem endereço absoluto do portal não há como montar o link");
        (await recuperacao.EnviarLinkPeloAdministradorAsync(usuario, novoUsuario: true)).Should().Be(ResultadoEnvioLinkSenha.Indisponivel);
        (await recuperacao.EnviarSenhaTemporariaAsync(usuario, "Temporaria#2026x")).Should().BeFalse();
        _fila.Mensagens.Should().BeEmpty();
        _usuarios.Tokens.Should().BeEmpty("nenhum código é emitido para um link que não seria enviado");

        endereco = "/conta"; // relativo continua recusado
        recuperacao.Disponivel.Should().BeFalse();

        endereco = "https://portalbase.exemplo.com.br/"; // configurado DEPOIS, na mesma sessão (mesmo escopo)
        (await recuperacao.EnviarLinkPeloAdministradorAsync(usuario, novoUsuario: true)).Should().Be(ResultadoEnvioLinkSenha.Enviado);
        var email = _fila.Mensagens.Should().ContainSingle().Subject;
        email.Html.Should().Contain("href=\"https://portalbase.exemplo.com.br/conta/redefinir-senha?token=");
        email.Texto.Should().Contain("https://portalbase.exemplo.com.br/conta/redefinir-senha?token=");
    }

    [Fact]
    public async Task Limite_De_Links_Por_Hora()
    {
        Usuario();
        for (var i = 0; i < 4; i++)
        {
            (await _recuperacao.SolicitarAsync("maria@exemplo.com")).Should().Be(ResultadoSolicitacaoRecuperacao.Aceita);
        }

        _fila.Mensagens.Should().HaveCount(3, "o 4º pedido na mesma hora não envia nada");

        _relogio.AgoraUtc = Inicio.AddMinutes(61);
        await _recuperacao.SolicitarAsync("maria@exemplo.com");
        _fila.Mensagens.Should().HaveCount(4, "passada a hora, volta a enviar");
    }

    [Fact]
    public async Task Redefinicao_Troca_A_Senha_Limpa_O_Bloqueio_Revoga_Sessoes_E_O_Link_Vale_Uma_Vez()
    {
        var usuario = Usuario();
        usuario.RegistrarFalhaAutenticacao(1, TimeSpan.FromMinutes(15), Inicio); // bloqueado por tentativas
        var stamp = usuario.SecurityStamp;
        await _recuperacao.SolicitarAsync("maria@exemplo.com");
        await _recuperacao.SolicitarAsync("maria@exemplo.com"); // um segundo link aberto

        (await _recuperacao.LinkValidoAsync("token-0")).Should().BeTrue();
        (await _recuperacao.RedefinirAsync("token-0", SenhaNova, SenhaNova)).Should().Be(ResultadoRedefinicaoSenha.Redefinida);

        usuario.HashSenha.Should().Be("H:" + SenhaNova);
        usuario.EstaImpedidoDeAutenticar(Inicio).Should().BeFalse("o bloqueio por tentativas é limpo");
        usuario.SecurityStamp.Should().NotBe(stamp, "as sessões abertas são revogadas");
        (await _recuperacao.RedefinirAsync("token-0", "Mais-Uma#Senha2026", "Mais-Uma#Senha2026")).Should().Be(ResultadoRedefinicaoSenha.LinkInvalido);
        (await _recuperacao.LinkValidoAsync("token-1")).Should().BeFalse("os outros links abertos são invalidados");
        _auditoria.Eventos.Should().Contain(e => e.Operacao == AcaoAuditoria.PasswordRecoveryCompleted && e.Resultado == ResultadoAuditoria.Permitido);
    }

    [Fact]
    public async Task Confirmacao_Diferente_E_Senha_Fraca_Nao_Consomem_O_Link()
    {
        var usuario = Usuario();
        await _recuperacao.SolicitarAsync("maria@exemplo.com");

        (await _recuperacao.RedefinirAsync("token-0", SenhaNova, SenhaNova + "x")).Should().Be(ResultadoRedefinicaoSenha.SenhasDiferentes);
        (await _recuperacao.RedefinirAsync("token-0", "fraca", "fraca")).Should().Be(ResultadoRedefinicaoSenha.SenhaForaDaPolitica);
        (await _recuperacao.RedefinirAsync("token-0", "Maria#Senha-2026", "Maria#Senha-2026")).Should().Be(ResultadoRedefinicaoSenha.SenhaForaDaPolitica, "contém o e-mail");

        usuario.HashSenha.Should().Be("H:" + SenhaAntiga);
        (await _recuperacao.LinkValidoAsync("token-0")).Should().BeTrue();
    }

    [Theory]
    [InlineData("nao-existe")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Link_Inexistente_E_Recusado(string? token)
    {
        Usuario();
        (await _recuperacao.RedefinirAsync(token, SenhaNova, SenhaNova)).Should().Be(ResultadoRedefinicaoSenha.LinkInvalido);
    }

    [Fact]
    public async Task Link_Expirado_E_Recusado()
    {
        var usuario = Usuario();
        await _recuperacao.SolicitarAsync("maria@exemplo.com");
        _relogio.AgoraUtc = Inicio.AddMinutes(31);

        (await _recuperacao.RedefinirAsync("token-0", SenhaNova, SenhaNova)).Should().Be(ResultadoRedefinicaoSenha.LinkInvalido);
        usuario.HashSenha.Should().Be("H:" + SenhaAntiga);
    }

    [Fact]
    public async Task Usuario_Inativado_Depois_Do_Pedido_Nao_Redefine()
    {
        var usuario = Usuario();
        await _recuperacao.SolicitarAsync("maria@exemplo.com");
        usuario.Inativar(Inicio);

        (await _recuperacao.RedefinirAsync("token-0", SenhaNova, SenhaNova)).Should().Be(ResultadoRedefinicaoSenha.LinkInvalido);
    }

    private sealed class FilaEmailFake : IFilaEmail
    {
        public bool Habilitado { get; set; } = true;

        public List<MensagemEmail> Mensagens { get; } = [];

        public bool Enfileirar(MensagemEmail mensagem)
        {
            Mensagens.Add(mensagem);
            return true;
        }
    }

    private sealed class RelogioAjustavel(DateTimeOffset agora) : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; set; } = agora;
    }
}
