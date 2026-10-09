using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>Troca de senha do usuário logado: exige a senha atual (com lockout), política, novo stamp e auditoria.</summary>
public sealed class AlterarSenhaTests
{
    private const string Atual = "Senha-Atual#2026";
    private const string Nova = "Senha-Nova#2026x";
    private static readonly DateTimeOffset Momento = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly AuditoriaFake _auditoria = new();
    private readonly AlterarSenha _alterar;
    private readonly Usuario _usuario;

    public AlterarSenhaTests()
    {
        _alterar = new AlterarSenha(_usuarios, new HasherSenhaFake(), new UnitOfWorkFake(), _auditoria, new RelogioFixo(Momento), new PoliticaAutenticacao(3, TimeSpan.FromMinutes(15)));
        _usuario = Usuario.Criar("maria", "Maria", "maria@exemplo.com", Momento);
        _usuario.DefinirCredencial("H:" + Atual, Momento);
        _usuarios.Usuarios.Add(_usuario);
    }

    [Fact]
    public async Task Troca_Com_A_Senha_Atual_Gera_Novo_Stamp_E_Audita()
    {
        var stamp = _usuario.SecurityStamp;

        var resultado = await _alterar.ExecutarAsync(_usuario.Id, Atual, Nova, Nova);

        resultado.Resultado.Should().Be(ResultadoAlteracaoSenha.Alterada);
        resultado.NovoSecurityStamp.Should().Be(_usuario.SecurityStamp).And.NotBe(stamp);
        _usuario.HashSenha.Should().Be("H:" + Nova);
        _auditoria.Eventos.Should().ContainSingle(e => e.Operacao == AcaoAuditoria.PasswordChanged && e.Resultado == ResultadoAuditoria.Permitido);
    }

    [Fact]
    public async Task Senha_Atual_Errada_Conta_Como_Tentativa_E_Bloqueia_Como_O_Login()
    {
        for (var i = 0; i < 3; i++)
        {
            (await _alterar.ExecutarAsync(_usuario.Id, "errada", Nova, Nova)).Resultado.Should().Be(ResultadoAlteracaoSenha.SenhaAtualIncorreta);
        }

        _usuario.EstaImpedidoDeAutenticar(Momento).Should().BeTrue("3 erros = bloqueio temporário, como no login");
        _usuario.HashSenha.Should().Be("H:" + Atual);
        var depois = async () => await _alterar.ExecutarAsync(_usuario.Id, Atual, Nova, Nova);
        await depois.Should().ThrowAsync<NaoAutenticadoException>("bloqueado, nem a senha certa troca");
    }

    [Theory]
    [InlineData(Nova, Nova + "x", ResultadoAlteracaoSenha.SenhasDiferentes)]
    [InlineData(Atual, Atual, ResultadoAlteracaoSenha.MesmaSenha)]
    [InlineData("fraca", "fraca", ResultadoAlteracaoSenha.SenhaForaDaPolitica)]
    [InlineData("Maria-2026#xyzw", "Maria-2026#xyzw", ResultadoAlteracaoSenha.SenhaForaDaPolitica)]
    public async Task Nova_Senha_Invalida_Nao_Troca(string nova, string confirmacao, ResultadoAlteracaoSenha esperado)
    {
        var stamp = _usuario.SecurityStamp;

        (await _alterar.ExecutarAsync(_usuario.Id, Atual, nova, confirmacao)).Resultado.Should().Be(esperado);

        _usuario.HashSenha.Should().Be("H:" + Atual);
        _usuario.SecurityStamp.Should().Be(stamp);
    }

    [Fact]
    public async Task Usuario_Inexistente_Ou_Inativo_Falha_Fechado()
    {
        var inexistente = async () => await _alterar.ExecutarAsync(Guid.NewGuid(), Atual, Nova, Nova);
        await inexistente.Should().ThrowAsync<NaoAutenticadoException>();

        _usuario.Inativar(Momento);
        var inativo = async () => await _alterar.ExecutarAsync(_usuario.Id, Atual, Nova, Nova);
        await inativo.Should().ThrowAsync<NaoAutenticadoException>();
    }
}
