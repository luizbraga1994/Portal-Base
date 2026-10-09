using FluentAssertions;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>Login pelo e-mail: sucesso, lockout e anti-enumeração (seções 4, 7, 26, 33).</summary>
public sealed class AutenticarUsuarioTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly UnitOfWorkFake _unitOfWork = new();
    private readonly AutenticarUsuario _autenticar;

    public AutenticarUsuarioTests()
        => _autenticar = new AutenticarUsuario(
            _usuarios,
            new HasherSenhaFake(),
            _unitOfWork,
            new AuditoriaFake(),
            new RelogioFixo(Momento),
            new PoliticaAutenticacao(3, TimeSpan.FromMinutes(15)));

    private Usuario ComCredencial(string senha = "SenhaCorreta123")
    {
        var usuario = Usuario.Criar("joana", "Joana", "joana@exemplo.com", Momento);
        usuario.DefinirCredencial("H:" + senha, Momento);
        _usuarios.Usuarios.Add(usuario);
        return usuario;
    }

    [Fact]
    public async Task Sucesso_Retorna_Identidade_E_Zera_Falhas()
    {
        ComCredencial();
        var resultado = await _autenticar.ExecutarAsync("joana@exemplo.com", "SenhaCorreta123");

        resultado.Login.Should().Be("joana");
        resultado.MfaHabilitado.Should().BeFalse();
        resultado.SecurityStamp.Should().NotBe(Guid.Empty);
    }

    [Fact] // anti-enumeração: mesma exceção para e-mail desconhecido
    public async Task Usuario_Inexistente_Lanca_Credenciais_Invalidas()
    {
        var acao = async () => await _autenticar.ExecutarAsync("naoexiste@exemplo.com", "qualquer");
        await acao.Should().ThrowAsync<CredenciaisInvalidasException>();
    }

    [Theory] // o e-mail é comparado na forma canônica: caixa e espaços nas pontas não importam
    [InlineData("JOANA@EXEMPLO.COM")]
    [InlineData("  Joana@Exemplo.com  ")]
    public async Task Email_Em_Outra_Caixa_Autentica_O_Mesmo_Usuario(string email)
    {
        var usuario = ComCredencial();

        var resultado = await _autenticar.ExecutarAsync(email, "SenhaCorreta123");

        resultado.UsuarioId.Should().Be(usuario.Id);
    }

    [Fact] // o login é pelo E-MAIL: o nome de usuário interno não autentica (resposta genérica, sem contar tentativa)
    public async Task Nome_De_Usuario_Nao_Autentica()
    {
        var usuario = ComCredencial();

        var acao = async () => await _autenticar.ExecutarAsync("joana", "SenhaCorreta123");

        await acao.Should().ThrowAsync<CredenciaisInvalidasException>();
        usuario.AcessosFalhosConsecutivos.Should().Be(0);
    }

    [Theory] // entrada hostil: vazia ou maior que qualquer e-mail gravável cai no MESMO caminho genérico
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Email_Vazio_Lanca_Credenciais_Invalidas(string? email)
    {
        ComCredencial();
        var acao = async () => await _autenticar.ExecutarAsync(email, "SenhaCorreta123");
        await acao.Should().ThrowAsync<CredenciaisInvalidasException>();
    }

    [Fact]
    public async Task Email_Acima_Do_Limite_Lanca_Credenciais_Invalidas()
    {
        ComCredencial();
        var longo = new string('a', Usuario.TamanhoMaximoEmail) + "@exemplo.com";

        var acao = async () => await _autenticar.ExecutarAsync(longo, "SenhaCorreta123");

        await acao.Should().ThrowAsync<CredenciaisInvalidasException>();
    }

    [Fact]
    public async Task Senha_Incorreta_Registra_Falha_E_Lanca_Credenciais_Invalidas()
    {
        var usuario = ComCredencial();
        var acao = async () => await _autenticar.ExecutarAsync("joana@exemplo.com", "errada");

        await acao.Should().ThrowAsync<CredenciaisInvalidasException>();
        usuario.AcessosFalhosConsecutivos.Should().Be(1);
    }

    [Fact] // §33.18 — tentativas repetidas → lockout
    public async Task Tentativas_Repetidas_Ativam_Lockout()
    {
        var usuario = ComCredencial();

        for (var i = 0; i < 3; i++)
        {
            try
            {
                await _autenticar.ExecutarAsync("joana@exemplo.com", "errada");
            }
            catch (CredenciaisInvalidasException)
            {
                // esperado
            }
        }

        usuario.EstaImpedidoDeAutenticar(Momento).Should().BeTrue();

        // Mesmo com a senha correta, durante o lockout o acesso é negado (resposta genérica).
        var comSenhaCorreta = async () => await _autenticar.ExecutarAsync("joana@exemplo.com", "SenhaCorreta123");
        await comSenhaCorreta.Should().ThrowAsync<CredenciaisInvalidasException>();
    }
}
