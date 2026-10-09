using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Trilha de auditoria com o NOME do ator (e não só o identificador), contra o MySQL real: a consulta autorizada traz
/// nome e login dos usuários; o filtro "Ator" aceita identificador, nome (sem diferenciar maiúsculas nem acentos), login
/// ou e-mail; e continua isolado por contribuinte. Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class AuditoriaNomeAtorMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public AuditoriaNomeAtorMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Trilha_Mostra_Nome_E_Login_Do_Usuario_E_Filtra_Por_Nome_Login_Email_Ou_Id()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, Cnpj());
        var outro = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, Cnpj());
        var (auditor, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "Senha-Forte#2026x");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, auditor, tenant, PapelConhecido.Administrador);

        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var joao = await CriarUsuarioAsync($"joao.{sufixo}", $"João Lima {sufixo}", $"Joao.{sufixo}@Exemplo.com");
        var operacao = $"TesteNomeAtor{sufixo}";
        await RegistrarAsync(joao.ToString(), tenant, operacao);
        await RegistrarAsync("worker-teste", tenant, operacao);
        await RegistrarAsync(joao.ToString(), outro, operacao); // outro contribuinte: nunca aparece

        using var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, auditor, mfaSatisfeito: false);
        var consulta = escopo.ServiceProvider.GetRequiredService<ConsultaAuditoriaAutorizada>();
        var filtro = new FiltroAuditoria { Tenant = "ignorado", Acao = operacao };

        var todos = await consulta.ConsultarAsync(tenant, filtro);
        todos.Itens.Should().HaveCount(2, "só os registros deste contribuinte");
        var doJoao = todos.Itens.Single(i => i.PrincipalId == joao.ToString());
        doJoao.NomeAtor.Should().Be($"João Lima {sufixo}");
        doJoao.LoginAtor.Should().Be($"joao.{sufixo}");
        todos.Itens.Single(i => i.PrincipalId == "worker-teste").NomeAtor.Should().BeNull("não é um usuário do catálogo");

        foreach (var texto in new[] { $"joao lima {sufixo}", $"JOÃO LIMA {sufixo}", $"JOAO.{sufixo}", $"joao.{sufixo}@exemplo.com".ToUpperInvariant(), joao.ToString() })
        {
            var achados = await consulta.ConsultarAsync(tenant, filtro with { Ator = texto });
            achados.Itens.Select(i => i.PrincipalId).Should().Equal([joao.ToString()], $"o filtro \"{texto}\" acha o João neste contribuinte");
        }

        (await consulta.ConsultarAsync(tenant, filtro with { Ator = "worker-teste" })).Itens.Should().ContainSingle("o identificador gravado continua valendo");
        (await consulta.ConsultarAsync(tenant, filtro with { Ator = $"ninguem-{sufixo}" })).Itens.Should().BeEmpty();
        (await consulta.ConsultarAsync(tenant, filtro with { Ator = "%_" })).Itens.Should().BeEmpty("curingas digitados valem como texto, não como 'qualquer um'");
    }

    private async Task<Guid> CriarUsuarioAsync(string login, string nome, string email)
    {
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var usuario = Usuario.Criar(login, nome, email, escopo.ServiceProvider.GetRequiredService<IRelogio>().AgoraUtc);
        catalogo.Usuarios.Add(usuario);
        await catalogo.SaveChangesAsync();
        return usuario.Id;
    }

    private async Task RegistrarAsync(string principal, Guid tenant, string operacao)
    {
        using var escopo = _fixture.CriarEscopo();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        await escopo.ServiceProvider.GetRequiredService<IAuditoria>().RegistrarAsync(
            new EventoAuditoria(principal, tenant.ToString(), operacao, ResultadoAuditoria.Permitido, relogio.AgoraUtc));
    }

    private static string Cnpj()
    {
        // 12 dígitos aleatórios + dígitos verificadores válidos.
        var digitos = Guid.NewGuid().ToByteArray().Take(12).Select(b => b % 10).ToList();
        digitos.Add(Dv(digitos, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        digitos.Add(Dv(digitos, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        return string.Concat(digitos);

        static int Dv(List<int> d, int[] pesos)
        {
            var resto = pesos.Select((p, i) => p * d[i]).Sum() % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }
}
