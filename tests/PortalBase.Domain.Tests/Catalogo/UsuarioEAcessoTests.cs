using FluentAssertions;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Tests.Catalogo;

public sealed class UsuarioEAcessoTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Usuario_Criar_ComecaAtivo()
    {
        var usuario = Usuario.Criar("jsilva", "João Silva", "joao@exemplo.com", Momento);

        usuario.Status.Should().Be(StatusUsuario.Ativo);
        usuario.Id.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("sem-arroba")]
    [InlineData("@sem-local.com")]
    [InlineData("sem-dominio@")]
    [InlineData("a@b@c.com")]
    [InlineData("com espaco@x.com")]
    public void Usuario_Criar_ComEmailInvalido_Lanca(string email)
    {
        var acao = () => Usuario.Criar("login", "Nome", email, Momento);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Theory]
    [InlineData("Joao@Exemplo.COM", "joao@exemplo.com")]
    [InlineData("  joao@exemplo.com  ", "joao@exemplo.com")]
    [InlineData("JOAO.SILVA+FISCAL@EXEMPLO.COM.BR", "joao.silva+fiscal@exemplo.com.br")]
    public void Usuario_Criar_GuardaEmailNaFormaCanonicaDoLogin(string informado, string esperado)
    {
        var usuario = Usuario.Criar("jsilva", "João", informado, Momento);

        usuario.Email.Should().Be(esperado);
        Usuario.NormalizarEmail(informado).Should().Be(esperado, "a busca do login usa a MESMA forma da gravação");
    }

    [Fact]
    public void Usuario_Criar_ComEmailAcimaDoLimite_Lanca()
    {
        var email = new string('a', Usuario.TamanhoMaximoEmail - "@x.com".Length + 1) + "@x.com";

        var acao = () => Usuario.Criar("login", "Nome", email, Momento);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Usuario_Bloquear_MudaStatusEStamp()
    {
        var usuario = Usuario.Criar("jsilva", "João", "joao@exemplo.com", Momento);
        var stamp = usuario.ConcurrencyStamp;

        usuario.Bloquear(Momento.AddMinutes(1));

        usuario.Status.Should().Be(StatusUsuario.Bloqueado);
        usuario.ConcurrencyStamp.Should().NotBe(stamp);
    }

    [Fact]
    public void UsuarioTenant_Conceder_ComecaAtivo()
    {
        var vinculo = UsuarioTenant.Conceder(Guid.NewGuid(), Guid.NewGuid(), Momento);

        vinculo.EstaAtivo.Should().BeTrue();
        vinculo.RevogadoEmUtc.Should().BeNull();
    }

    [Fact]
    public void UsuarioTenant_Revogar_MudaStatusERegistraData()
    {
        var vinculo = UsuarioTenant.Conceder(Guid.NewGuid(), Guid.NewGuid(), Momento);

        vinculo.Revogar(Momento.AddDays(1));

        vinculo.EstaAtivo.Should().BeFalse();
        vinculo.Status.Should().Be(StatusAcesso.Revogado);
        vinculo.RevogadoEmUtc.Should().Be(Momento.AddDays(1));
    }

    [Fact]
    public void UsuarioTenant_Conceder_ComGuidVazio_Lanca()
    {
        var acao = () => UsuarioTenant.Conceder(Guid.Empty, Guid.NewGuid(), Momento);

        acao.Should().Throw<ArgumentException>();
    }
}
