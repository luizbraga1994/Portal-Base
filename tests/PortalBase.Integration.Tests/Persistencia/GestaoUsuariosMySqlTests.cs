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
/// Cadastro de usuários e perfis e o perfil global "Administrador da plataforma" contra o MySQL real: cadastrar (com
/// senha ou link), anti-escalada, autoconcessão bloqueada, conta compartilhada entre contribuintes protegida, exclusão,
/// perfis personalizados isolados por contribuinte, o acesso total (sem vínculo, sem MFA) do administrador da
/// plataforma, a senha temporária enviada por e-mail e o MFA exigido/removido pelo administrador. Pulado sem
/// <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class GestaoUsuariosMySqlTests
{
    private const string Senha = "Senha-Forte#2026x";
    private readonly FixtureMultiTenant _fixture;

    public GestaoUsuariosMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Administrador_Cadastra_Usuario_Com_Senha_Inicial_E_Ele_Entra_Com_O_Perfil_Escolhido()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var admin = await CriarComPapelAsync(tenant, PapelConhecido.Administrador);
        var leitor = await PerfilDoSistemaAsync(admin, tenant, PapelConhecido.Leitor);
        var sufixo = Sufixo();

        ResultadoGestao criado;
        using (var c = Como(admin))
        {
            criado = await c.Usuarios.CriarAsync(tenant, new NovoUsuario($"maria.{sufixo}", "Maria Souza", $"Maria.{sufixo}@Exemplo.com", Senha, [leitor], false));
        }

        criado.Desfecho.Should().Be(DesfechoGestao.Ok, criado.Mensagem);
        var novo = criado.Id!.Value;
        using (var escopo = _fixture.CriarEscopo())
        {
            var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();
            (await autenticar.ExecutarAsync($"maria.{sufixo}@exemplo.com", Senha)).UsuarioId.Should().Be(novo, "o e-mail é guardado na forma canônica");
            var permissoes = await escopo.ServiceProvider.GetRequiredService<IRepositorioUsuario>().ObterPermissoesAsync(novo, tenant);
            permissoes.Chaves.Should().BeEquivalentTo([Permissoes.DadosVisualizar]);
            (await Auditoria(escopo, AcaoAuditoria.UserCreated, novo)).Should().ContainSingle(r => r.PrincipalId == admin.ToString());
        }

        using (var c = Como(admin))
        {
            (await c.Usuarios.CriarAsync(tenant, new NovoUsuario($"outra.{sufixo}", "Outra", $"MARIA.{sufixo}@exemplo.com", Senha, [leitor], false)))
                .Desfecho.Should().Be(DesfechoGestao.Conflito, "o e-mail (login) é único, sem diferenciar maiúsculas");
            (await c.Usuarios.CriarAsync(tenant, new NovoUsuario($"fraca.{sufixo}", "Fraca", $"fraca.{sufixo}@exemplo.com", "curta", [leitor], false)))
                .Desfecho.Should().Be(DesfechoGestao.Invalido, "a senha inicial segue a política");
            (await c.Usuarios.CriarAsync(tenant, new NovoUsuario("com espaço", "X", $"x.{sufixo}@exemplo.com", Senha, [leitor], false)))
                .Desfecho.Should().Be(DesfechoGestao.Invalido);
            (await c.Usuarios.CriarAsync(tenant, new NovoUsuario($"semperfil.{sufixo}", "X", $"y.{sufixo}@exemplo.com", Senha, [], false)))
                .Desfecho.Should().Be(DesfechoGestao.Invalido, "pelo menos um perfil");
        }
    }

    [SkippableFact]
    public async Task Cadastro_Sem_Senha_Envia_Link_De_Boas_Vindas_Que_Cria_A_Senha()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var admin = await CriarComPapelAsync(tenant, PapelConhecido.Administrador);
        var leitor = await PerfilDoSistemaAsync(admin, tenant, PapelConhecido.Leitor);
        var fila = new FilaCaptura();
        var email = $"convite.{Sufixo()}@exemplo.com";

        ResultadoGestao criado;
        using (var c = Como(admin, fila))
        {
            criado = await c.Usuarios.CriarAsync(tenant, new NovoUsuario(email.Split('@')[0], "Convidado", email, null, [leitor], EnviarLinkSenha: true));
        }

        criado.Desfecho.Should().Be(DesfechoGestao.Ok, criado.Mensagem);
        var mensagem = fila.Mensagens.Should().ContainSingle().Subject;
        mensagem.Para.Should().Be(email);
        mensagem.Assunto.Should().Contain("crie sua senha");

        using (var escopo = _fixture.CriarEscopo())
        {
            var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();
            var antes = async () => await autenticar.ExecutarAsync(email, Senha);
            await antes.Should().ThrowAsync<CredenciaisInvalidasException>("sem senha ainda, ninguém entra");
            (await Recuperacao(escopo, fila).RedefinirAsync(ExtrairToken(mensagem), Senha, Senha)).Should().Be(ResultadoRedefinicaoSenha.Redefinida);
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            (await escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>().ExecutarAsync(email, Senha)).UsuarioId.Should().Be(criado.Id!.Value);
        }
    }

    [SkippableFact]
    public async Task Ninguem_Concede_Permissao_Que_Nao_Tem()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var admin = await CriarComPapelAsync(tenant, PapelConhecido.Administrador);
        var leitor = await PerfilDoSistemaAsync(admin, tenant, PapelConhecido.Leitor);
        var administrador = await PerfilDoSistemaAsync(admin, tenant, PapelConhecido.Administrador);
        var sufixo = Sufixo();

        // Um "gestor de usuários": só administra usuários e vê dados.
        Guid perfilGestor;
        using (var c = Como(admin))
        {
            var r = await c.Perfis.CriarAsync(tenant, $"Gestor {sufixo}", null, [Permissoes.AdminUsuarios, Permissoes.DadosVisualizar]);
            r.Desfecho.Should().Be(DesfechoGestao.Ok, r.Mensagem);
            perfilGestor = r.Id!.Value;
            var g = await c.Usuarios.CriarAsync(tenant, new NovoUsuario($"gestor.{sufixo}", "Gestor", $"gestor.{sufixo}@exemplo.com", Senha, [perfilGestor], false));
            g.Desfecho.Should().Be(DesfechoGestao.Ok, g.Mensagem);
            perfilGestor = g.Id!.Value; // reaproveita a variável: agora é o id do USUÁRIO gestor
        }

        var gestor = perfilGestor;
        using (var c = Como(gestor))
        {
            var escalada = await c.Usuarios.CriarAsync(tenant, new NovoUsuario($"cumplice.{sufixo}", "Cúmplice", $"cumplice.{sufixo}@exemplo.com", Senha, [administrador], false));
            escalada.Desfecho.Should().Be(DesfechoGestao.NaoPermitido);
            escalada.Mensagem.Should().Contain(Permissoes.DadosExcluir);

            (await c.Perfis.CriarAsync(tenant, $"Atalho {sufixo}", null, [Permissoes.DadosExcluir])).Desfecho.Should().Be(DesfechoGestao.NaoPermitido);
            (await c.Usuarios.CriarAsync(tenant, new NovoUsuario($"leitor.{sufixo}", "Leitor", $"leitor.{sufixo}@exemplo.com", Senha, [leitor], false)))
                .Desfecho.Should().Be(DesfechoGestao.Ok, "o que o gestor tem, ele pode conceder");
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            (await catalogo.Usuarios.AnyAsync(u => u.Login == $"cumplice.{sufixo}")).Should().BeFalse("nada foi gravado");
            (await catalogo.RegistrosAuditoria.AsNoTracking()
                .CountAsync(r => r.PrincipalId == gestor.ToString() && r.Operacao == AcaoAuditoria.PrivilegeEscalationAttempt && r.Resultado == nameof(ResultadoAuditoria.Negado)))
                .Should().Be(2);
        }
    }

    [SkippableFact]
    public async Task Ninguem_Altera_O_Proprio_Privilegio_Nem_Se_Exclui()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var admin = await CriarComPapelAsync(tenant, PapelConhecido.Administrador);
        var leitor = await PerfilDoSistemaAsync(admin, tenant, PapelConhecido.Leitor);

        using var c = Como(admin);
        var perfis = async () => await c.Usuarios.DefinirPerfisAsync(tenant, admin, [leitor]);
        await perfis.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
        var excluir = async () => await c.Usuarios.ExcluirAsync(tenant, admin);
        await excluir.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
        var inativar = async () => await c.Usuarios.DefinirSituacaoAsync(tenant, admin, ativo: false);
        await inativar.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
        var revogar = async () => await c.Escopo.ServiceProvider.GetRequiredService<GestaoAcesso>().RevogarAcessoAsync(tenant, admin);
        await revogar.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();

        // A própria conta: nome e e-mail podem ser corrigidos.
        (await c.Usuarios.AlterarDadosAsync(tenant, admin, "Admin Renomeado", $"admin.{Sufixo()}@exemplo.com")).Desfecho.Should().Be(DesfechoGestao.Ok);
    }

    [SkippableFact]
    public async Task Conta_Compartilhada_Com_Outro_Contribuinte_So_Muda_Pelo_Administrador_Da_Plataforma()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var adminA = await CriarComPapelAsync(a, PapelConhecido.Administrador);
        var (compartilhado, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, compartilhado, a, PapelConhecido.Leitor);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, compartilhado, b, PapelConhecido.Leitor);
        var novoEmail = $"tomada.{Sufixo()}@exemplo.com";

        using (var c = Como(adminA))
        {
            // Trocar o e-mail (o login) de quem também é de B permitiria tomar a conta pelo "Esqueci a senha".
            (await c.Usuarios.AlterarDadosAsync(a, compartilhado, "Outro Nome", novoEmail)).Desfecho.Should().Be(DesfechoGestao.NaoPermitido);
            (await c.Usuarios.DefinirSituacaoAsync(a, compartilhado, ativo: false)).Desfecho.Should().Be(DesfechoGestao.NaoPermitido);

            // Excluir de A: some de A, continua ativo e com acesso a B.
            (await c.Usuarios.ExcluirAsync(a, compartilhado)).Desfecho.Should().Be(DesfechoGestao.Ok);
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var conta = await catalogo.Usuarios.AsNoTracking().SingleAsync(u => u.Id == compartilhado);
            conta.Email.Should().NotBe(novoEmail);
            conta.Status.Should().Be(StatusUsuario.Ativo);
            (await catalogo.UsuarioTenants.AnyAsync(v => v.UsuarioId == compartilhado && v.TenantId == a)).Should().BeFalse();
            (await catalogo.UsuarioTenants.AnyAsync(v => v.UsuarioId == compartilhado && v.TenantId == b && v.Status == StatusAcesso.Ativo)).Should().BeTrue();
        }

        // O Administrador da plataforma (sem vínculo com B) pode.
        var plataforma = await CriarAdministradorPlataformaAsync();
        using (var c = Como(plataforma))
        {
            (await c.Usuarios.AlterarDadosAsync(b, compartilhado, "Nome Corrigido", novoEmail)).Desfecho.Should().Be(DesfechoGestao.Ok);
        }
    }

    [SkippableFact]
    public async Task Excluir_Usuario_So_Deste_Contribuinte_Inativa_A_Conta()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var admin = await CriarComPapelAsync(tenant, PapelConhecido.Administrador);
        var (alvo, login) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, alvo, tenant, PapelConhecido.Analista);

        using (var c = Como(admin))
        {
            (await c.Usuarios.ExcluirAsync(tenant, alvo)).Desfecho.Should().Be(DesfechoGestao.Ok);
            (await c.Usuarios.ExcluirAsync(tenant, alvo)).Desfecho.Should().Be(DesfechoGestao.NaoEncontrado, "já não está neste contribuinte");
        }

        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        (await catalogo.Usuarios.AsNoTracking().SingleAsync(u => u.Id == alvo)).Status.Should().Be(StatusUsuario.Inativo, "a conta fica (auditoria), inativa");
        (await catalogo.UsuarioTenants.AnyAsync(v => v.UsuarioId == alvo)).Should().BeFalse();
        var entrar = async () => await escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>().ExecutarAsync(login + "@exemplo.com", Senha);
        await entrar.Should().ThrowAsync<CredenciaisInvalidasException>();
    }

    [SkippableFact]
    public async Task Perfil_Personalizado_Define_As_Permissoes_E_Fica_No_Seu_Contribuinte()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var adminA = await CriarComPapelAsync(a, PapelConhecido.Administrador);
        var adminB = await CriarComPapelAsync(b, PapelConhecido.Administrador);
        var sufixo = Sufixo();
        var leitorA = await PerfilDoSistemaAsync(adminA, a, PapelConhecido.Leitor);

        Guid contador;
        Guid usuario;
        using (var c = Como(adminA))
        {
            var r = await c.Perfis.CriarAsync(a, "Contador", "Fecha o mês", [Permissoes.DadosVisualizar, Permissoes.DadosImportar]);
            r.Desfecho.Should().Be(DesfechoGestao.Ok, r.Mensagem);
            contador = r.Id!.Value;
            usuario = (await c.Usuarios.CriarAsync(a, new NovoUsuario($"cont.{sufixo}", "Contador", $"cont.{sufixo}@exemplo.com", Senha, [contador], false))).Id!.Value;

            (await c.Perfis.CriarAsync(a, "ADMINISTRADOR", null, [Permissoes.DadosVisualizar])).Desfecho.Should().Be(DesfechoGestao.Conflito, "nome de perfil do sistema");
            (await c.Perfis.CriarAsync(a, $"Plataforma {sufixo}", null, [Permissoes.BackupExecutar])).Desfecho.Should().Be(DesfechoGestao.Invalido, "permissão de plataforma não entra em perfil");
            (await c.Perfis.AlterarAsync(a, leitorA, "Leitor 2", null, [Permissoes.DadosVisualizar])).Desfecho.Should().Be(DesfechoGestao.NaoPermitido, "perfil do sistema não muda");
        }

        await PermissoesDeveSerAsync(usuario, a, Permissoes.DadosVisualizar, Permissoes.DadosImportar);

        using (var c = Como(adminA))
        {
            (await c.Perfis.AlterarAsync(a, contador, "Contador", "Só consulta", [Permissoes.DadosVisualizar])).Desfecho.Should().Be(DesfechoGestao.Ok);
        }

        await PermissoesDeveSerAsync(usuario, a, Permissoes.DadosVisualizar);

        using (var c = Como(adminB))
        {
            (await c.Perfis.ListarAsync(b)).Should().NotContain(p => p.Id == contador, "perfil de A não aparece em B");
            (await c.Perfis.CriarAsync(b, "Contador", null, [Permissoes.DadosVisualizar])).Desfecho.Should().Be(DesfechoGestao.Ok, "o mesmo nome em outro contribuinte é outro perfil");
            (await c.Perfis.AlterarAsync(b, contador, "Invadido", null, [Permissoes.DadosVisualizar])).Desfecho.Should().Be(DesfechoGestao.NaoEncontrado);
            (await c.Usuarios.CriarAsync(b, new NovoUsuario($"x.{sufixo}", "X", $"x.{sufixo}@exemplo.com", Senha, [contador], false)))
                .Desfecho.Should().Be(DesfechoGestao.Invalido, "perfil de outro contribuinte é tratado como inexistente");
        }

        using (var c = Como(adminA))
        {
            (await c.Perfis.ExcluirAsync(a, contador)).Desfecho.Should().Be(DesfechoGestao.Conflito, "em uso");
            (await c.Usuarios.DefinirPerfisAsync(a, usuario, [leitorA])).Desfecho.Should().Be(DesfechoGestao.Ok);
            (await c.Perfis.ExcluirAsync(a, contador)).Desfecho.Should().Be(DesfechoGestao.Ok);
        }
    }

    [SkippableFact]
    public async Task Administrador_Da_Plataforma_Acessa_Todos_Os_Contribuintes_Com_Todas_As_Permissoes_Sem_Mfa()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var ativo = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var desabilitado = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        await CenarioIdentidade.DesabilitarTenantAsync(_fixture, desabilitado);
        var plataforma = await CriarAdministradorPlataformaAsync();
        var comum = await CriarComPapelAsync(ativo, PapelConhecido.Administrador);

        using (var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, plataforma, mfaSatisfeito: false))
        {
            var sp = escopo.ServiceProvider;
            var tenant = sp.GetRequiredService<IAutorizacaoTenant>();
            (await tenant.PodeAcessarAsync(plataforma.ToString(), ativo)).Should().BeTrue("sem vínculo, por ser administrador da plataforma");
            (await tenant.PodeAcessarAsync(plataforma.ToString(), desabilitado)).Should().BeFalse("contribuinte não operacional continua fechado");
            (await tenant.PodeAdministrarAsync(plataforma.ToString(), desabilitado)).Should().BeTrue();

            var permissoes = await sp.GetRequiredService<IRepositorioUsuario>().ObterPermissoesAsync(plataforma, ativo);
            permissoes.Chaves.Should().Contain(Enum.GetValues<PermissaoConhecida>().Select(CatalogoPapeisPermissoes.ChaveDe));

            var acessos = await sp.GetRequiredService<IConsultaAcessosUsuario>().ListarDoUsuarioAsync(plataforma);
            acessos.Select(x => x.TenantId).Should().Contain([ativo, desabilitado]);

            // Alto impacto sem MFA: liberado no contribuinte e na plataforma.
            await sp.GetRequiredService<IServicoAutorizacao>().ExigirAsync(ativo, Permissoes.DadosExcluir);
            await sp.GetRequiredService<IAutorizacaoPlataforma>().ExigirAsync(Permissoes.RestauracaoExecutar);
        }

        // Controle: um Administrador de contribuinte continua precisando do MFA no alto impacto e não vê a plataforma.
        using (var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, comum, mfaSatisfeito: false))
        {
            var sp = escopo.ServiceProvider;
            var excluir = async () => await sp.GetRequiredService<IServicoAutorizacao>().ExigirAsync(ativo, Permissoes.DadosExcluir);
            await excluir.Should().ThrowAsync<MfaRequeridoException>();
            (await sp.GetRequiredService<IAutorizacaoPlataforma>().AvaliarAsync(Permissoes.RestauracaoExecutar)).Permitido.Should().BeFalse();
            (await sp.GetRequiredService<IAutorizacaoTenant>().PodeAcessarAsync(comum.ToString(), desabilitado)).Should().BeFalse();
        }
    }

    [SkippableFact]
    public async Task So_O_Administrador_Da_Plataforma_Concede_O_Perfil_E_Nunca_A_Si_Mesmo()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var adminTenant = await CriarComPapelAsync(tenant, PapelConhecido.Administrador);
        var (alvo, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        var plataforma = await CriarAdministradorPlataformaAsync();
        var stampAntes = await StampAsync(alvo);

        using (var c = Como(adminTenant))
        {
            var conceder = async () => await c.Usuarios.DefinirAdministradorPlataformaAsync(alvo, true);
            await conceder.Should().ThrowAsync<AutorizacaoNegadaException>("administrador de contribuinte não governa a plataforma");
        }

        using (var c = Como(plataforma))
        {
            (await c.Usuarios.DefinirAdministradorPlataformaAsync(alvo, true)).Desfecho.Should().Be(DesfechoGestao.Ok);
            var aSiMesmo = async () => await c.Usuarios.DefinirAdministradorPlataformaAsync(plataforma, false);
            await aSiMesmo.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            (await escopo.ServiceProvider.GetRequiredService<IRepositorioUsuario>().EhAdministradorPlataformaAsync(alvo)).Should().BeTrue();
            (await StampAsync(alvo)).Should().NotBe(stampAntes, "a sessão do usuário cai e a próxima já nasce com o perfil");
            (await Auditoria(escopo, AcaoAuditoria.PlatformAdminGranted, alvo)).Should().ContainSingle(r => r.Tenant == EscoposAuditoria.Plataforma);
        }

        using (var c = Como(plataforma))
        {
            (await c.Usuarios.DefinirAdministradorPlataformaAsync(alvo, false)).Desfecho.Should().Be(DesfechoGestao.Ok);
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            (await escopo.ServiceProvider.GetRequiredService<IRepositorioUsuario>().EhAdministradorPlataformaAsync(alvo)).Should().BeFalse();
        }
    }

    [SkippableFact]
    public async Task Senha_Temporaria_Vai_So_Por_Email_E_Exige_A_Troca_No_Proximo_Acesso()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var admin = await CriarComPapelAsync(tenant, PapelConhecido.Administrador);
        var leitor = await PerfilDoSistemaAsync(admin, tenant, PapelConhecido.Leitor);
        var fila = new FilaCaptura();
        var email = $"temp.{Sufixo()}@exemplo.com";
        const string Digitada = "Ignorada#2026xyz";

        ResultadoGestao criado;
        using (var c = Como(admin, fila))
        {
            criado = await c.Usuarios.CriarAsync(tenant,
                new NovoUsuario(email.Split('@')[0], "Temporária", email, Digitada, [leitor], EnviarLinkSenha: true, GerarSenhaTemporaria: true));
        }

        criado.Desfecho.Should().Be(DesfechoGestao.Ok, criado.Mensagem);
        var usuario = criado.Id!.Value;
        var primeira = fila.Mensagens.Should().ContainSingle("só o e-mail da senha temporária — nada de link").Subject;
        primeira.Para.Should().Be(email);
        primeira.Assunto.Should().Contain("senha temporária");
        var senha1 = ExtrairSenhaTemporaria(primeira);
        PoliticaSenha.Validar(senha1, email.Split('@')[0], email).Should().BeNull("a senha gerada cumpre a política");
        criado.Mensagem.Should().NotContain(senha1, "a senha nunca aparece na tela");

        using (var escopo = _fixture.CriarEscopo())
        {
            var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();
            var digitada = async () => await autenticar.ExecutarAsync(email, Digitada);
            await digitada.Should().ThrowAsync<CredenciaisInvalidasException>("a senha temporária tem precedência sobre a digitada");
            var entrada = await autenticar.ExecutarAsync(email, senha1);
            entrada.TrocaSenhaPendente.Should().BeTrue();
            entrada.CadastroMfaPendente.Should().BeFalse();
        }

        // Gerar outra pelo cadastro: a anterior deixa de valer e as sessões caem.
        var stamp = await StampAsync(usuario);
        using (var c = Como(admin, fila))
        {
            (await c.Usuarios.GerarSenhaTemporariaAsync(tenant, usuario)).Desfecho.Should().Be(DesfechoGestao.Ok);
            var aSiMesmo = async () => await c.Usuarios.GerarSenhaTemporariaAsync(tenant, admin);
            await aSiMesmo.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
        }

        fila.Mensagens.Should().HaveCount(2);
        var senha2 = ExtrairSenhaTemporaria(fila.Mensagens[1]);
        senha2.Should().NotBe(senha1);
        (await StampAsync(usuario)).Should().NotBe(stamp);

        using (var escopo = _fixture.CriarEscopo())
        {
            var sp = escopo.ServiceProvider;
            var antiga = async () => await sp.GetRequiredService<AutenticarUsuario>().ExecutarAsync(email, senha1);
            await antiga.Should().ThrowAsync<CredenciaisInvalidasException>();

            // O usuário cria a senha definitiva: a exigência some.
            (await sp.GetRequiredService<AlterarSenha>().ExecutarAsync(usuario, senha2, Senha, Senha)).Resultado.Should().Be(ResultadoAlteracaoSenha.Alterada);
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var sp = escopo.ServiceProvider;
            (await sp.GetRequiredService<AutenticarUsuario>().ExecutarAsync(email, Senha)).TrocaSenhaPendente.Should().BeFalse();

            var catalogo = sp.GetRequiredService<PortalBaseCatalogoDbContext>();
            (await catalogo.Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario)).TrocarSenhaNoProximoAcesso.Should().BeFalse();
            (await Auditoria(escopo, AcaoAuditoria.TemporaryPasswordIssued, usuario)).Should().ContainSingle(r => r.PrincipalId == admin.ToString());
            (await Auditoria(escopo, AcaoAuditoria.UserCreated, usuario)).Should().ContainSingle()
                .Which.ValoresNovos.Should().NotBeNull();
            var criacao = Valores((await Auditoria(escopo, AcaoAuditoria.UserCreated, usuario)).Single().ValoresNovos);
            criacao.Should().Contain("primeiroAcesso", "senha temporária por e-mail").And.Contain("segundoFatorExigido", "não");

            // A senha não vai para a trilha de auditoria (regra 8).
            var trilha = await catalogo.RegistrosAuditoria.AsNoTracking()
                .Where(r => r.RecursoId == usuario.ToString() || r.PrincipalId == usuario.ToString())
                .ToListAsync();
            trilha.Should().NotBeEmpty();
            foreach (var registro in trilha)
            {
                string.Concat(registro.Motivo, registro.ValoresAnteriores, registro.ValoresNovos, registro.Metadados)
                    .Should().NotContain(senha1).And.NotContain(senha2);
            }
        }
    }

    [SkippableFact]
    public async Task Senha_Temporaria_Exige_Email_Ligado_E_Conta_Que_O_Administrador_Pode_Gerir()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var adminA = await CriarComPapelAsync(a, PapelConhecido.Administrador);
        var leitor = await PerfilDoSistemaAsync(adminA, a, PapelConhecido.Leitor);
        var (compartilhado, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, compartilhado, a, PapelConhecido.Leitor);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, compartilhado, b, PapelConhecido.Leitor);
        var stamp = await StampAsync(compartilhado);

        var desligada = new FilaCaptura { Ligada = false };
        using (var c = Como(adminA, desligada))
        {
            var email = $"sem.email.{Sufixo()}@exemplo.com";
            (await c.Usuarios.CriarAsync(a, new NovoUsuario(email.Split('@')[0], "Sem e-mail", email, null, [leitor], false, GerarSenhaTemporaria: true)))
                .Desfecho.Should().Be(DesfechoGestao.Invalido, "sem envio de e-mail não há como entregar a senha");
        }

        var fila = new FilaCaptura();
        using (var c = Como(adminA, fila))
        {
            (await c.Usuarios.GerarSenhaTemporariaAsync(a, compartilhado)).Desfecho.Should().Be(DesfechoGestao.NaoPermitido,
                "trocar a senha de quem também é de outro contribuinte é tomada de conta");
            (await c.Usuarios.GerarSenhaTemporariaAsync(a, Guid.NewGuid())).Desfecho.Should().Be(DesfechoGestao.NaoEncontrado);
        }

        fila.Mensagens.Should().BeEmpty();
        (await StampAsync(compartilhado)).Should().Be(stamp, "nada mudou na conta compartilhada");
    }

    [SkippableFact]
    public async Task Mfa_Exigido_Leva_Ao_Cadastro_E_So_E_Removido_Com_Step_Up_Pelo_Administrador()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CnpjNovo());
        var admin = await CriarComPapelAsync(a, PapelConhecido.Administrador);
        var (alvo, login) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, alvo, a, PapelConhecido.Leitor);
        var email = login + "@exemplo.com";

        // Exigir o MFA (não derruba a sessão: vale no próximo login).
        var stamp = await StampAsync(alvo);
        using (var c = Como(admin))
        {
            (await c.Usuarios.DefinirMfaObrigatorioAsync(a, alvo, true)).Desfecho.Should().Be(DesfechoGestao.Ok);
            (await c.Usuarios.DefinirMfaObrigatorioAsync(a, alvo, true)).Mensagem.Should().Contain("já era exigido");
            var aSiMesmo = async () => await c.Usuarios.DefinirMfaObrigatorioAsync(a, admin, false);
            await aSiMesmo.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
        }

        (await StampAsync(alvo)).Should().Be(stamp);
        (await EntrarAsync(email)).CadastroMfaPendente.Should().BeTrue("o próximo login leva ao cadastro do autenticador");

        // O usuário cadastra o autenticador (segredo protegido + 3 códigos de recuperação).
        await CadastrarMfaAsync(alvo, codigos: 3);
        var entrada = await EntrarAsync(email);
        entrada.CadastroMfaPendente.Should().BeFalse();
        entrada.MfaHabilitado.Should().BeTrue();

        using (var c = Como(admin))
        {
            var semStepUp = async () => await c.Usuarios.RemoverMfaAsync(a, alvo);
            await semStepUp.Should().ThrowAsync<MfaRequeridoException>("remover o MFA exige o MFA de quem remove");
        }

        stamp = await StampAsync(alvo);
        using (var c = Como(admin, mfaSatisfeito: true))
        {
            var aSiMesmo = async () => await c.Usuarios.RemoverMfaAsync(a, admin);
            await aSiMesmo.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
            (await c.Usuarios.RemoverMfaAsync(a, alvo)).Desfecho.Should().Be(DesfechoGestao.Ok);
            (await c.Usuarios.RemoverMfaAsync(a, alvo)).Mensagem.Should().Contain("não tem MFA cadastrado", "repetir não faz nada");
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var conta = await catalogo.Usuarios.AsNoTracking().SingleAsync(u => u.Id == alvo);
            conta.MfaHabilitado.Should().BeFalse();
            conta.SegredoMfaProtegido.Should().BeNull();
            conta.SecurityStamp.Should().NotBe(stamp, "as sessões do usuário caem");
            (await catalogo.CodigosRecuperacaoMfa.CountAsync(x => x.UsuarioId == alvo)).Should().Be(0, "os códigos de recuperação também são apagados");
            (await Auditoria(escopo, AcaoAuditoria.MfaRemovedByAdministrator, alvo)).Should().ContainSingle(r => r.PrincipalId == admin.ToString())
                .Which.ValoresNovos.Should().NotBeNull();
            Valores((await Auditoria(escopo, AcaoAuditoria.MfaRemovedByAdministrator, alvo)).Single().ValoresNovos)
                .Should().Contain("codigosReservaApagados", "3");
            var exigencia = (await Auditoria(escopo, AcaoAuditoria.MfaRequirementChanged, alvo)).Should().ContainSingle().Subject;
            Valores(exigencia.ValoresAnteriores).Should().Contain("segundoFatorExigido", "não");
            Valores(exigencia.ValoresNovos).Should().Contain("segundoFatorExigido", "sim");
        }

        (await EntrarAsync(email)).CadastroMfaPendente.Should().BeTrue("continua exigido: cadastra um novo autenticador no próximo acesso");

        // Conta compartilhada com outro contribuinte: o administrador de A não mexe no MFA dela.
        await CenarioIdentidade.ConcederPapelAsync(_fixture, alvo, b, PapelConhecido.Leitor);
        await CadastrarMfaAsync(alvo, codigos: 1);
        using (var c = Como(admin, mfaSatisfeito: true))
        {
            (await c.Usuarios.RemoverMfaAsync(a, alvo)).Desfecho.Should().Be(DesfechoGestao.NaoPermitido);
            (await c.Usuarios.DefinirMfaObrigatorioAsync(a, alvo, false)).Desfecho.Should().Be(DesfechoGestao.NaoPermitido);
        }

        // ...mas o Administrador da plataforma (com a dispensa do segundo fator) pode.
        var plataforma = await CriarAdministradorPlataformaAsync();
        using (var c = Como(plataforma, mfaSatisfeito: true))
        {
            (await c.Usuarios.RemoverMfaAsync(b, alvo)).Desfecho.Should().Be(DesfechoGestao.Ok);
        }
    }

    private async Task<ResultadoAutenticacao> EntrarAsync(string email)
    {
        using var escopo = _fixture.CriarEscopo();
        return await escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>().ExecutarAsync(email, Senha);
    }

    // Simula o autenticador cadastrado (o fluxo da tela é coberto no E2E): segredo protegido + códigos de recuperação.
    private async Task CadastrarMfaAsync(Guid usuario, int codigos)
    {
        using var escopo = _fixture.CriarEscopo();
        var sp = escopo.ServiceProvider;
        var catalogo = sp.GetRequiredService<PortalBaseCatalogoDbContext>();
        var conta = await catalogo.Usuarios.SingleAsync(u => u.Id == usuario);
        var agora = DateTimeOffset.UtcNow;
        conta.PrepararMfa(await sp.GetRequiredService<IProtetorSegredoMfa>().ProtegerAsync(sp.GetRequiredService<IProvedorTotp>().GerarSegredo()), agora);
        conta.ConfirmarMfa(agora);
        catalogo.CodigosRecuperacaoMfa.AddRange(Enumerable.Range(0, codigos).Select(_ => CodigoRecuperacaoMfa.Gerar(usuario, "hash-" + Guid.NewGuid().ToString("N"), agora)));
        await catalogo.SaveChangesAsync();
    }

    // ------------------------------------------------------------------------------------------------- apoio

    private sealed class Contexto : IDisposable
    {
        public required IServiceScope Escopo { get; init; }

        public required GestaoUsuarios Usuarios { get; init; }

        public required GestaoPerfis Perfis { get; init; }

        public void Dispose() => Escopo.Dispose();
    }

    private Contexto Como(Guid ator, FilaCaptura? fila = null, bool mfaSatisfeito = false)
    {
        var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, ator, mfaSatisfeito);
        var sp = escopo.ServiceProvider;
        return new Contexto
        {
            Escopo = escopo,
            Usuarios = new GestaoUsuarios(
                sp.GetRequiredService<IServicoAutorizacao>(),
                sp.GetRequiredService<IAutorizacaoPlataforma>(),
                sp.GetRequiredService<IContextoSeguranca>(),
                sp.GetRequiredService<IRepositorioGestaoUsuarios>(),
                sp.GetRequiredService<IRepositorioUsuario>(),
                sp.GetRequiredService<IHasherSenha>(),
                Recuperacao(escopo, fila ?? new FilaCaptura()),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IAuditoria>(),
                sp.GetRequiredService<IRelogio>(),
                sp.GetRequiredService<IGeradorTokens>()),
            Perfis = new GestaoPerfis(
                sp.GetRequiredService<IServicoAutorizacao>(),
                sp.GetRequiredService<IContextoSeguranca>(),
                sp.GetRequiredService<IRepositorioGestaoUsuarios>(),
                sp.GetRequiredService<IRepositorioUsuario>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IAuditoria>(),
                sp.GetRequiredService<IRelogio>()),
        };
    }

    private static RecuperacaoSenha Recuperacao(IServiceScope escopo, IFilaEmail fila)
    {
        var sp = escopo.ServiceProvider;
        return new RecuperacaoSenha(
            sp.GetRequiredService<IRepositorioUsuario>(),
            sp.GetRequiredService<IGeradorTokens>(),
            sp.GetRequiredService<IHashDeterministico>(),
            sp.GetRequiredService<IHasherSenha>(),
            fila,
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IAuditoria>(),
            sp.GetRequiredService<IRelogio>(),
            OpcoesRecuperacaoSenha.Padrao("https://portal.exemplo.com.br"));
    }

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
        using var c = Como(ator);
        return (await c.Perfis.ListarAsync(tenant)).Single(p => p.DoSistema && p.Nome == papel.ToString()).Id;
    }

    private async Task PermissoesDeveSerAsync(Guid usuario, Guid tenant, params string[] esperadas)
    {
        using var escopo = _fixture.CriarEscopo();
        var permissoes = await escopo.ServiceProvider.GetRequiredService<IRepositorioUsuario>().ObterPermissoesAsync(usuario, tenant);
        permissoes.Chaves.Should().BeEquivalentTo(esperadas);
    }

    private async Task<Guid> StampAsync(Guid usuario)
    {
        using var escopo = _fixture.CriarEscopo();
        return (await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario)).SecurityStamp;
    }

    private static async Task<List<RegistroAuditoria>> Auditoria(IServiceScope escopo, string operacao, Guid recurso)
        => await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().RegistrosAuditoria.AsNoTracking()
            .Where(r => r.Operacao == operacao && r.RecursoId == recurso.ToString())
            .ToListAsync();

    private static string ExtrairToken(MensagemEmail mensagem)
    {
        const string Marcador = "token=";
        var inicio = mensagem.Texto.IndexOf(Marcador, StringComparison.Ordinal) + Marcador.Length;
        var fim = mensagem.Texto.IndexOfAny([' ', '\n', '\r'], inicio);
        return Uri.UnescapeDataString(mensagem.Texto[inicio..fim]);
    }

    private static Dictionary<string, string?> Valores(string? json)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(json ?? "{}") ?? [];

    // A senha vem numa linha própria, recuada, no texto do e-mail (é o que o usuário copia).
    private static string ExtrairSenhaTemporaria(MensagemEmail mensagem)
        => mensagem.Texto.Split('\n').Single(l => l.StartsWith("    ", StringComparison.Ordinal)).Trim();

    private static string Sufixo() => Guid.NewGuid().ToString("N")[..8];

    private static string CnpjNovo()
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

    private sealed class FilaCaptura : IFilaEmail
    {
        public bool Ligada { get; init; } = true;

        public bool Habilitado => Ligada;

        public List<MensagemEmail> Mensagens { get; } = [];

        public bool Enfileirar(MensagemEmail mensagem)
        {
            if (!Ligada)
            {
                return false;
            }

            lock (Mensagens)
            {
                Mensagens.Add(mensagem);
            }

            return true;
        }
    }
}
