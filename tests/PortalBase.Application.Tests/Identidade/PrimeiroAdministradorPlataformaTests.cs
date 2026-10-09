using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Identidade;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>
/// Primeiro Administrador da plataforma de uma implantação nova (comando no servidor): só enquanto não existe nenhum;
/// e-mail válido e único; senha pela política (guardada só como hash); auditado com quem rodou o comando.
/// </summary>
public sealed class PrimeiroAdministradorPlataformaTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string SenhaForte = "Portal#DeRE-2026!x";

    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly UnitOfWorkFake _uow = new();
    private readonly AuditoriaFake _auditoria = new();

    private PrimeiroAdministradorPlataforma Criar() => new(_usuarios, new HasherSenhaFake(), _uow, _auditoria, new RelogioFixo(Agora));

    [Fact]
    public async Task Cria_O_Primeiro_Com_Senha_Em_Hash_E_Auditoria()
    {
        var r = await Criar().CriarAsync(" Maria Souza ", " Maria@AAConsulting.com.br ", SenhaForte, @"SERVIDOR\admin");

        r.Sucesso.Should().BeTrue();
        var usuario = _usuarios.Usuarios.Should().ContainSingle().Subject;
        usuario.Email.Should().Be("maria@aaconsulting.com.br");
        usuario.Login.Should().Be("maria@aaconsulting.com.br");
        usuario.AdministradorPlataforma.Should().BeTrue();
        usuario.HashSenha.Should().Be("H:" + SenhaForte).And.NotBe(SenhaForte);
        r.UsuarioId.Should().Be(usuario.Id);
        _auditoria.Eventos.Select(e => e.Operacao).Should().Equal(AcaoAuditoria.UserCreated, AcaoAuditoria.PlatformAdminGranted);
        _auditoria.Eventos.Should().OnlyContain(e => e.PrincipalId == @"servidor:SERVIDOR\admin");
    }

    [Fact]
    public async Task Com_Administrador_Existente_Recusa_Sem_Gravar()
    {
        var existente = Usuario.Criar("outro@x.com", "Outro", "outro@x.com", Agora);
        existente.DefinirAdministradorPlataforma(true, Agora);
        _usuarios.Usuarios.Add(existente);

        (await Criar().JaExisteAsync()).Should().BeTrue();
        var r = await Criar().CriarAsync("Maria", "maria@x.com", SenhaForte, "admin");

        r.Sucesso.Should().BeFalse();
        r.Mensagem.Should().Contain("Já existe Administrador");
        _usuarios.Usuarios.Should().ContainSingle();
        _auditoria.Eventos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "maria@x.com", SenhaForte, "Informe o nome")]
    [InlineData("Maria", "nao-e-email", SenhaForte, "e-mail válido")]
    [InlineData("Maria", "maria@x.com", "curta1!", "entre 12")]
    [InlineData("Maria", "maria@x.com", "minusculasapenas", "3 destes 4")]
    public async Task Dados_Invalidos_Nao_Gravam(string nome, string email, string senha, string trecho)
    {
        var r = await Criar().CriarAsync(nome, email, senha, "admin");

        r.Sucesso.Should().BeFalse();
        r.Mensagem.Should().Contain(trecho).And.NotContain(senha.Length > 0 ? senha : "§");
        _usuarios.Usuarios.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_Ja_Usado_Recusa()
    {
        _usuarios.Usuarios.Add(Usuario.Criar("maria", "Maria", "maria@x.com", Agora));

        var r = await Criar().CriarAsync("Maria", "MARIA@x.com", SenhaForte, "admin");

        r.Sucesso.Should().BeFalse();
        r.Mensagem.Should().Contain("Já existe um usuário com este e-mail");
        _usuarios.Usuarios.Should().ContainSingle().Which.AdministradorPlataforma.Should().BeFalse();
    }
}
