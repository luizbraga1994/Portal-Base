using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Notificacoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Acesso de UMA conta a VÁRIOS contribuintes (pedido de 08/10/2026), contra o catálogo MySQL real: o Administrador da
/// plataforma dá, ajusta e revoga o acesso em vários contribuintes de uma vez (mesmos perfis do sistema em todos, auditado
/// em cada um); o administrador de um contribuinte só enxerga e só mexe nos que ele administra (contribuinte forjado no
/// pedido é recusado sem mudar nada); perfil personalizado não vale para vários; ninguém mexe no próprio acesso; usuário
/// de outro contribuinte é invisível. Contribuintes só no catálogo (sem base física). Pulado sem MySQL de teste.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class ContribuintesDoUsuarioMySqlTests
{
    private const string Senha = "Senha-Forte#2026x";
    private readonly FixtureMultiTenant _fixture;

    public ContribuintesDoUsuarioMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Administrador_Da_Plataforma_Da_Ajusta_E_Revoga_Acesso_Em_Varios_Contribuintes_De_Uma_Vez()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (a, b, c) = (await NovoTenantAsync(), await NovoTenantAsync(), await NovoTenantAsync());
        var plataforma = await CriarAdministradorPlataformaAsync();
        var usuario = await CriarComPapelAsync(a, PapelConhecido.Leitor);
        var analista = await PerfilDoSistemaAsync(plataforma, a, PapelConhecido.Analista);
        var leitor = await PerfilDoSistemaAsync(plataforma, a, PapelConhecido.Leitor);

        using (var ctx = Como(plataforma))
        {
            var lista = await ctx.Contribuintes.ListarDoUsuarioAsync(a, usuario);
            lista.Should().NotBeNull();
            lista!.Should().Contain(x => x.TenantId == a && x.Atual && x.Situacao == SituacaoNoContribuinte.Ativo);
            lista.Should().Contain(x => x.TenantId == b && !x.Atual && x.Situacao == SituacaoNoContribuinte.SemAcesso);
            lista[0].TenantId.Should().Be(a, "o contribuinte da sessão vem primeiro");

            var salvo = await ctx.Contribuintes.SalvarAsync(a, usuario, [a, b, c], [analista]);
            salvo.Desfecho.Should().Be(DesfechoGestao.Ok, salvo.Mensagem);
        }

        await PermissoesDeveSerAsync(usuario, a, Permissoes.DadosVisualizar); // o da sessão não muda por aqui
        (await PerfisAsync(usuario, b)).Should().BeEquivalentTo([analista]);
        (await PerfisAsync(usuario, c)).Should().BeEquivalentTo([analista]);
        (await AuditoriaAsync(AcaoAuditoria.TenantUserLinked, usuario)).Select(r => r.Tenant).Should().BeEquivalentTo([b.ToString(), c.ToString()]);
        using (var escopo = _fixture.CriarEscopo())
        {
            (await escopo.ServiceProvider.GetRequiredService<IConsultaAcessosUsuario>().ListarDoUsuarioAsync(usuario))
                .Select(x => x.TenantId).Should().BeEquivalentTo([a, b, c], "a mesma conta entra nos três");
        }

        var stampAntes = await StampAsync(usuario);
        using (var ctx = Como(plataforma))
        {
            // Desmarca B (revoga) e troca os perfis dos marcados para Leitor.
            var salvo = await ctx.Contribuintes.SalvarAsync(a, usuario, [c], [leitor]);
            salvo.Desfecho.Should().Be(DesfechoGestao.Ok, salvo.Mensagem);
            salvo.Mensagem.Should().Contain("acesso revogado").And.Contain("perfis ajustados");

            var lista = await ctx.Contribuintes.ListarDoUsuarioAsync(a, usuario);
            lista!.Single(x => x.TenantId == b).Situacao.Should().Be(SituacaoNoContribuinte.Revogado);
            lista!.Single(x => x.TenantId == c).Perfis.Select(p => p.Id).Should().BeEquivalentTo([leitor]);

            (await ctx.Contribuintes.SalvarAsync(a, usuario, [c], [leitor])).Mensagem.Should().StartWith("Nada a mudar", "repetir o pedido não muda nada");

            // Marca B de novo: o acesso revogado é devolvido.
            (await ctx.Contribuintes.SalvarAsync(a, usuario, [b, c], [leitor])).Desfecho.Should().Be(DesfechoGestao.Ok);
            (await ctx.Contribuintes.ListarDoUsuarioAsync(a, usuario))!.Single(x => x.TenantId == b).Situacao.Should().Be(SituacaoNoContribuinte.Ativo);
        }

        (await StampAsync(usuario)).Should().NotBe(stampAntes, "revogar encerra as sessões do usuário");
        (await AuditoriaAsync(AcaoAuditoria.AccessRevoked, usuario)).Should().ContainSingle(r => r.Tenant == b.ToString() && r.PrincipalId == plataforma.ToString());
        (await AuditoriaAsync(AcaoAuditoria.RoleAssigned, usuario)).Should().Contain(r => r.Tenant == c.ToString());
    }

    [SkippableFact]
    public async Task Administrador_De_Contribuinte_So_Ve_E_So_Muda_Os_Contribuintes_Que_Administra()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (a, b, alheio) = (await NovoTenantAsync(), await NovoTenantAsync(), await NovoTenantAsync());
        var admin = await CriarComPapelAsync(a, PapelConhecido.Administrador);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, admin, b, PapelConhecido.Administrador);
        var usuario = await CriarComPapelAsync(a, PapelConhecido.Leitor);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, alheio, PapelConhecido.Leitor);
        var leitor = await PerfilDoSistemaAsync(admin, a, PapelConhecido.Leitor);

        using var ctx = Como(admin);
        var lista = await ctx.Contribuintes.ListarDoUsuarioAsync(a, usuario);
        lista!.Select(x => x.TenantId).Should().BeEquivalentTo([a, b], "o contribuinte que ele não administra nem aparece");

        // Pedido forjado com o contribuinte alheio: recusado, e nada muda em lugar nenhum.
        var forjado = await ctx.Contribuintes.SalvarAsync(a, usuario, [b, alheio], [leitor]);
        forjado.Desfecho.Should().Be(DesfechoGestao.NaoPermitido);
        (await VinculoAsync(usuario, b)).Should().BeNull();
        (await VinculoAsync(usuario, alheio))!.Status.Should().Be(StatusAcesso.Ativo, "o acesso no contribuinte alheio fica como estava");

        // Desmarcar não alcança o alheio (não está na lista dele): só B ganha acesso.
        (await ctx.Contribuintes.SalvarAsync(a, usuario, [b], [leitor])).Desfecho.Should().Be(DesfechoGestao.Ok);
        (await VinculoAsync(usuario, b))!.Status.Should().Be(StatusAcesso.Ativo);
        (await VinculoAsync(usuario, alheio))!.Status.Should().Be(StatusAcesso.Ativo);
    }

    [SkippableFact]
    public async Task Perfil_Personalizado_Proprio_Acesso_E_Usuario_De_Outro_Contribuinte_Sao_Recusados()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (a, b) = (await NovoTenantAsync(), await NovoTenantAsync());
        var plataforma = await CriarAdministradorPlataformaAsync();
        var admin = await CriarComPapelAsync(a, PapelConhecido.Administrador);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, admin, b, PapelConhecido.Administrador);
        var usuario = await CriarComPapelAsync(a, PapelConhecido.Leitor);
        var deOutro = await CriarComPapelAsync(b, PapelConhecido.Leitor);

        Guid personalizado;
        using (var ctx = Como(plataforma))
        {
            var criado = await ctx.Perfis.CriarAsync(a, $"Consulta {Guid.NewGuid():N}"[..20], null, [Permissoes.DadosVisualizar]);
            criado.Desfecho.Should().Be(DesfechoGestao.Ok, criado.Mensagem);
            personalizado = criado.Id!.Value;
        }

        using (var ctx = Como(admin))
        {
            var recusado = await ctx.Contribuintes.SalvarAsync(a, usuario, [b], [personalizado]);
            recusado.Desfecho.Should().Be(DesfechoGestao.Invalido);
            recusado.Mensagem.Should().Contain("perfis do sistema");
            (await VinculoAsync(usuario, b)).Should().BeNull();

            var proprio = async () => await ctx.Contribuintes.SalvarAsync(a, admin, [a, b], []);
            await proprio.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();

            (await ctx.Contribuintes.ListarDoUsuarioAsync(a, deOutro)).Should().BeNull("usuário que não é deste contribuinte é invisível daqui");
            (await ctx.Contribuintes.SalvarAsync(a, deOutro, [b], [])).Desfecho.Should().Be(DesfechoGestao.NaoEncontrado);
        }
    }

    [SkippableFact]
    public async Task Cadastro_Novo_Ja_Entra_Nos_Outros_Contribuintes_Escolhidos()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (a, b) = (await NovoTenantAsync(), await NovoTenantAsync());
        var plataforma = await CriarAdministradorPlataformaAsync();
        var analista = await PerfilDoSistemaAsync(plataforma, a, PapelConhecido.Analista);
        var sufixo = Guid.NewGuid().ToString("N")[..8];

        using var ctx = Como(plataforma);
        (await ctx.Contribuintes.ListarAdministradosAsync(a)).Select(x => x.TenantId).Should().Contain([a, b]);
        var criado = await ctx.Usuarios.CriarAsync(a, new NovoUsuario($"multi.{sufixo}", "Multi", $"multi.{sufixo}@exemplo.com", Senha, [analista], false));
        criado.Desfecho.Should().Be(DesfechoGestao.Ok, criado.Mensagem);

        var outros = await ctx.Contribuintes.DarAcessoEmOutrosAsync(a, criado.Id!.Value, [a, b], [analista]);
        outros.Desfecho.Should().Be(DesfechoGestao.Ok, outros.Mensagem);
        (await PerfisAsync(criado.Id.Value, b)).Should().BeEquivalentTo([analista]);
    }

    // ------------------------------------------------------------------------------------------------- apoio

    private sealed class Contexto : IDisposable
    {
        public required IServiceScope Escopo { get; init; }

        public required GestaoUsuarios Usuarios { get; init; }

        public required GestaoPerfis Perfis { get; init; }

        public required GestaoContribuintesDoUsuario Contribuintes { get; init; }

        public void Dispose() => Escopo.Dispose();
    }

    private Contexto Como(Guid ator)
    {
        var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, ator, mfaSatisfeito: false);
        var sp = escopo.ServiceProvider;
        var usuarios = new GestaoUsuarios(
            sp.GetRequiredService<IServicoAutorizacao>(),
            sp.GetRequiredService<IAutorizacaoPlataforma>(),
            sp.GetRequiredService<IContextoSeguranca>(),
            sp.GetRequiredService<IRepositorioGestaoUsuarios>(),
            sp.GetRequiredService<IRepositorioUsuario>(),
            sp.GetRequiredService<IHasherSenha>(),
            new RecuperacaoSenha(
                sp.GetRequiredService<IRepositorioUsuario>(),
                sp.GetRequiredService<IGeradorTokens>(),
                sp.GetRequiredService<IHashDeterministico>(),
                sp.GetRequiredService<IHasherSenha>(),
                new FilaDescartada(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IAuditoria>(),
                sp.GetRequiredService<IRelogio>(),
                OpcoesRecuperacaoSenha.Padrao("https://portal.exemplo.com.br")),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IAuditoria>(),
            sp.GetRequiredService<IRelogio>(),
            sp.GetRequiredService<IGeradorTokens>());
        return new Contexto
        {
            Escopo = escopo,
            Usuarios = usuarios,
            Perfis = new GestaoPerfis(
                sp.GetRequiredService<IServicoAutorizacao>(),
                sp.GetRequiredService<IContextoSeguranca>(),
                sp.GetRequiredService<IRepositorioGestaoUsuarios>(),
                sp.GetRequiredService<IRepositorioUsuario>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IAuditoria>(),
                sp.GetRequiredService<IRelogio>()),
            Contribuintes = new GestaoContribuintesDoUsuario(
                usuarios,
                sp.GetRequiredService<IServicoAutorizacao>(),
                sp.GetRequiredService<IConsultaAcessosUsuario>(),
                sp.GetRequiredService<IRepositorioGestaoUsuarios>(),
                sp.GetRequiredService<IRepositorioUsuario>(),
                sp.GetRequiredService<IContextoSeguranca>()),
        };
    }

    private Task<Guid> NovoTenantAsync() => CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());

    private async Task<Guid> CriarComPapelAsync(Guid tenant, PapelConhecido papel)
    {
        var (id, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, id, tenant, papel);
        return id;
    }

    private async Task<Guid> CriarAdministradorPlataformaAsync()
    {
        var (id, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var usuario = await catalogo.Usuarios.SingleAsync(u => u.Id == id);
        usuario.DefinirAdministradorPlataforma(true, DateTimeOffset.UtcNow);
        await catalogo.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> PerfilDoSistemaAsync(Guid ator, Guid tenant, PapelConhecido papel)
    {
        using var ctx = Como(ator);
        return (await ctx.Perfis.ListarAsync(tenant)).Single(p => p.DoSistema && p.Nome == papel.ToString()).Id;
    }

    private async Task PermissoesDeveSerAsync(Guid usuario, Guid tenant, params string[] esperadas)
    {
        using var escopo = _fixture.CriarEscopo();
        var permissoes = await escopo.ServiceProvider.GetRequiredService<IRepositorioUsuario>().ObterPermissoesAsync(usuario, tenant);
        permissoes.Chaves.Should().BeEquivalentTo(esperadas);
    }

    private async Task<List<Guid>> PerfisAsync(Guid usuario, Guid tenant)
    {
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        return await (
                from v in catalogo.UsuarioTenants.AsNoTracking()
                join p in catalogo.UsuarioTenantPapeis.AsNoTracking() on v.Id equals p.UsuarioTenantId
                where v.UsuarioId == usuario && v.TenantId == tenant && v.Status == StatusAcesso.Ativo
                select p.PapelId)
            .ToListAsync();
    }

    private async Task<UsuarioTenant?> VinculoAsync(Guid usuario, Guid tenant)
    {
        using var escopo = _fixture.CriarEscopo();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().UsuarioTenants.AsNoTracking()
            .SingleOrDefaultAsync(v => v.UsuarioId == usuario && v.TenantId == tenant);
    }

    private async Task<Guid> StampAsync(Guid usuario)
    {
        using var escopo = _fixture.CriarEscopo();
        return (await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario)).SecurityStamp;
    }

    private async Task<List<RegistroAuditoria>> AuditoriaAsync(string operacao, Guid recurso)
    {
        using var escopo = _fixture.CriarEscopo();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().RegistrosAuditoria.AsNoTracking()
            .Where(r => r.Operacao == operacao && r.RecursoId == recurso.ToString())
            .ToListAsync();
    }

    private static string CnpjNovo()
    {
        // 12 dígitos aleatórios + dígitos verificadores válidos (fictício).
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

    private sealed class FilaDescartada : IFilaEmail
    {
        public bool Habilitado => false;

        public bool Enfileirar(MensagemEmail mensagem) => false;
    }
}
