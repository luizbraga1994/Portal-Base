using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.AcessoLocal;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Ferramentas;

/// <summary>
/// Comando de acesso LOCAL (tools/PortalBase.AcessoLocal) contra MySQL real, com a DI de produção da fixture:
/// cria contribuinte provisionado/ativo + usuário (hash) + vínculo/papéis + MFA, tudo auditado; é idempotente;
/// entrada inválida não grava NADA; contribuinte desabilitado e vínculo revogado são recusados; a senha nunca
/// aparece na saída. O login e a autorização são provados pelos casos de uso reais (AutenticarUsuario,
/// GestaoMfa, IServicoAutorizacao). Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class AcessoLocalIntegracaoTests
{
    private const string Senha = "Senha-Local#2026";
    private const string OutraSenha = "Outra-Senha#2027";

    private readonly FixtureMultiTenant _fixture;

    public AcessoLocalIntegracaoTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Cria_Contribuinte_Provisionado_Usuario_Vinculo_Papel_E_Mfa_Com_Auditoria()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        var terminal = new TerminalFalso().ResponderOculto(Senha, Senha);

        var resultado = await ExecutarAsync(terminal, ator, Entrada(cnpj, login) with { AtivarMfa = true });

        resultado.Should().Be(ResultadoAcessoLocal.Sucesso, terminal.Texto);
        var tenant = await TenantPorCnpjAsync(cnpj);
        tenant.Should().NotBeNull();
        tenant!.Estado.Should().Be(EstadoProvisionamento.Ativo);
        tenant.SchemaVersion.Should().NotBeNullOrWhiteSpace("a base foi provisionada e migrada de verdade");

        // Nome ESCOLHIDO (informado em minúsculas) vira MAIÚSCULO e define a base portalbase_tenant_<nome>.
        var nomeEsperado = NomeTenantDe(login).ToUpperInvariant();
        tenant.Nome.Should().Be(nomeEsperado);
        tenant.NomeBaseFisica.Should().Be("portalbase_tenant_" + NomeTenantDe(login));
        terminal.Texto.Should().Contain(nomeEsperado).And.Contain(tenant.NomeBaseFisica);
        terminal.Texto.Should().Contain($"login        : {EmailDe(login)}", "o resumo diz com que e-mail se entra");
        using (var escopo = _fixture.CriarEscopo())
        {
            var admin = escopo.ServiceProvider.GetRequiredService<IAdministradorBancoTenant>();
            (await admin.BancoExisteAsync(tenant.NomeBaseFisica)).Should().BeTrue();
            (await admin.ObterProprietarioAsync(tenant.NomeBaseFisica))
                .Should().Be(new ProprietarioBaseTenant(false, tenant.Id, cnpj), "a base fica marcada com o dono dos dados");
        }

        var usuario = await UsuarioPorLoginAsync(login);
        usuario!.MfaHabilitado.Should().BeTrue();
        usuario.HashSenha.Should().NotBeNullOrWhiteSpace().And.NotContain(Senha);
        usuario.AdministradorPlataforma.Should().BeTrue("o Administrador do acesso local também administra a plataforma");
        terminal.Texto.Should().Contain("Administrador da plataforma");

        // Login real: senha certa autentica (com MFA exigido); senha errada não.
        using (var escopo = _fixture.CriarEscopo())
        {
            var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();
            var autenticado = await autenticar.ExecutarAsync(EmailDe(login), Senha);
            autenticado.UsuarioId.Should().Be(usuario.Id);
            autenticado.MfaHabilitado.Should().BeTrue();

            var errada = async () => await autenticar.ExecutarAsync(EmailDe(login), OutraSenha);
            await errada.Should().ThrowAsync<CredenciaisInvalidasException>();
        }

        // Os códigos de recuperação exibidos são reais (segundo fator aceito pelo caso de uso).
        var recuperacao = terminal.CodigosRecuperacaoExibidos();
        recuperacao.Should().NotBeEmpty();
        using (var escopo = _fixture.CriarEscopo())
        {
            var validar = async () => await escopo.ServiceProvider.GetRequiredService<GestaoMfa>().ValidarAsync(usuario.Id, recuperacao[0]);
            await validar.Should().NotThrowAsync();
        }

        // Autorização server-side: Administrador com MFA administra usuários deste contribuinte.
        using (var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, usuario.Id, mfaSatisfeito: true))
        {
            var exigir = async () => await escopo.ServiceProvider.GetRequiredService<IServicoAutorizacao>()
                .ExigirAsync(tenant.Id, Permissoes.AdminUsuarios);
            await exigir.Should().NotThrowAsync();
        }

        // Auditoria: cada alteração com o ator da ferramenta, a origem e UMA correlação por execução.
        var eventos = await AuditoriaDoAtorAsync(ator);
        eventos.Select(e => e.Operacao).Should().Contain(
        [
            "TenantCriado", AcaoAuditoria.TenantActivated, AcaoAuditoria.UserCreated,
            AcaoAuditoria.TenantUserLinked, AcaoAuditoria.RoleAssigned, AcaoAuditoria.PlatformAdminGranted,
        ]);
        eventos.Should().OnlyContain(e => e.Origem == CriadorAcessoLocal.OrigemAuditoria);
        var correlacoes = eventos.Select(e => e.CorrelationId).Distinct().ToList();
        correlacoes.Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        terminal.Texto.Should().Contain(correlacoes[0]!, "o resumo informa a correlação para achar os eventos");

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var mfa = await catalogo.RegistrosAuditoria.AsNoTracking()
                .Where(r => r.PrincipalId == usuario.Id.ToString() && r.Operacao == "MfaAtivacaoConfirmada")
                .ToListAsync();
            mfa.Should().ContainSingle(r => r.Resultado == nameof(ResultadoAuditoria.Permitido) && r.CorrelationId == correlacoes[0]);
        }

        // A senha não aparece em lugar nenhum: nem na saída do terminal, nem na trilha de auditoria.
        terminal.Texto.Should().NotContain(Senha);
        eventos.Should().OnlyContain(e => !(e.Motivo ?? string.Empty).Contains(Senha, StringComparison.Ordinal)
            && !(e.Metadados ?? string.Empty).Contains(Senha, StringComparison.Ordinal)
            && !(e.ValoresNovos ?? string.Empty).Contains(Senha, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Reexecucao_Nao_Duplica_Nada_Nao_Troca_A_Senha_E_Nao_Audita()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        var tenantId = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);

        (await ExecutarAsync(new TerminalFalso(interativo: false), ator, EntradaAutomacao(cnpj, login, Senha)))
            .Should().Be(ResultadoAcessoLocal.Sucesso);
        var eventosAntes = (await AuditoriaDoAtorAsync(ator)).Count;

        // Segunda execução com OUTRA senha e sem --redefinir-senha: nada muda.
        var segunda = new TerminalFalso(interativo: false);
        (await ExecutarAsync(segunda, ator, EntradaAutomacao(cnpj, login, OutraSenha)))
            .Should().Be(ResultadoAcessoLocal.Sucesso, segunda.Texto);

        segunda.Texto.Should().Contain("senha foi mantida");
        (await AuditoriaDoAtorAsync(ator)).Should().HaveCount(eventosAntes, "reexecutar sem mudança não gera eventos");
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var cnpjValor = Cnpj.Criar(cnpj);
        (await catalogo.Tenants.CountAsync(t => t.Cnpj == cnpjValor)).Should().Be(1);
        var usuarios = await catalogo.Usuarios.Where(u => u.Login == login).ToListAsync();
        usuarios.Should().ContainSingle();
        var vinculos = await catalogo.UsuarioTenants.Where(v => v.UsuarioId == usuarios[0].Id && v.TenantId == tenantId).ToListAsync();
        vinculos.Should().ContainSingle();
        (await catalogo.UsuarioTenantPapeis.CountAsync(p => p.UsuarioTenantId == vinculos[0].Id)).Should().Be(1);

        var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();
        (await autenticar.ExecutarAsync(EmailDe(login), Senha)).UsuarioId.Should().Be(usuarios[0].Id);
    }

    [SkippableFact]
    public async Task Email_De_Login_De_Outro_Usuario_E_Recusado_Sem_Gravar()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);
        (await ExecutarAsync(new TerminalFalso(interativo: false), ator, EntradaAutomacao(cnpj, login, Senha)))
            .Should().Be(ResultadoAcessoLocal.Sucesso);

        // Outro usuário pedindo o MESMO e-mail (em outra caixa): o e-mail é o login, então é recusado antes de gravar.
        var outroLogin = login + "_b";
        var terminal = new TerminalFalso(interativo: false);
        var entrada = EntradaAutomacao(cnpj, outroLogin, Senha) with { Email = EmailDe(login).ToUpperInvariant() };
        (await ExecutarAsync(terminal, ator, entrada)).Should().Be(ResultadoAcessoLocal.EntradaInvalida, terminal.Texto);

        terminal.Texto.Should().Contain($"já é o login do usuário '{login}'").And.Contain("nada foi gravado");
        (await UsuarioPorLoginAsync(outroLogin)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Usuario_Existente_Entra_Com_O_Email_Que_Ja_Tem_E_O_Comando_Avisa()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);
        (await ExecutarAsync(new TerminalFalso(interativo: false), ator, EntradaAutomacao(cnpj, login, Senha)))
            .Should().Be(ResultadoAcessoLocal.Sucesso);

        var terminal = new TerminalFalso(interativo: false);
        var entrada = EntradaAutomacao(cnpj, login, Senha) with { Email = "outro." + EmailDe(login) };
        (await ExecutarAsync(terminal, ator, entrada)).Should().Be(ResultadoAcessoLocal.Sucesso, terminal.Texto);

        terminal.Texto.Should().Contain($"já existe com o e-mail {EmailDe(login)}")
            .And.Contain($"login        : {EmailDe(login)}");
        (await UsuarioPorLoginAsync(login))!.Email.Should().Be(EmailDe(login), "o --email informado não altera o cadastro");
    }

    [SkippableFact]
    public async Task Redefinir_Senha_Troca_A_Credencial_E_Audita()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);
        (await ExecutarAsync(new TerminalFalso(interativo: false), ator, EntradaAutomacao(cnpj, login, Senha)))
            .Should().Be(ResultadoAcessoLocal.Sucesso);

        var terminal = new TerminalFalso().ResponderOculto(OutraSenha, OutraSenha);
        var entrada = new EntradaAcessoLocal(cnpj, login, [PapelConhecido.Administrador]) { RedefinirSenha = true, AtivarMfa = false };
        (await ExecutarAsync(terminal, ator, entrada)).Should().Be(ResultadoAcessoLocal.Sucesso, terminal.Texto);

        using (var escopo = _fixture.CriarEscopo())
        {
            var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();
            var antiga = async () => await autenticar.ExecutarAsync(EmailDe(login), Senha);
            await antiga.Should().ThrowAsync<CredenciaisInvalidasException>();
            (await autenticar.ExecutarAsync(EmailDe(login), OutraSenha)).Login.Should().Be(login);
        }

        (await AuditoriaDoAtorAsync(ator)).Should().ContainSingle(e => e.Operacao == AcaoAuditoria.PasswordChanged);
        terminal.Texto.Should().NotContain(OutraSenha);
    }

    [SkippableTheory]
    [InlineData("senha-fraca")]
    [InlineData("email-invalido")]
    [InlineData("nome-ausente-sem-terminal")]
    [InlineData("confirmacao-divergente")]
    [InlineData("tenant-ausente-sem-terminal")]
    [InlineData("tenant-invalido")]
    public async Task Entrada_Invalida_Nao_Grava_Nada(string caso)
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        var (terminal, entrada) = caso switch
        {
            "senha-fraca" => (new TerminalFalso(interativo: false), EntradaAutomacao(cnpj, login, "curta")),
            "email-invalido" => (new TerminalFalso(interativo: false), EntradaAutomacao(cnpj, login, Senha) with { Email = "não-é-email" }),
            "nome-ausente-sem-terminal" => (new TerminalFalso(interativo: false), EntradaAutomacao(cnpj, login, Senha) with { Nome = null }),
            "tenant-ausente-sem-terminal" => (new TerminalFalso(interativo: false), EntradaAutomacao(cnpj, login, Senha) with { Tenant = null }),
            "tenant-invalido" => (new TerminalFalso(interativo: false), EntradaAutomacao(cnpj, login, Senha) with { Tenant = "stake-01" }),
            _ => (new TerminalFalso().ResponderOculto(Senha, "x", Senha, "y", Senha, "z"), Entrada(cnpj, login) with { AtivarMfa = false }),
        };

        var resultado = await ExecutarAsync(terminal, ator, entrada);

        resultado.Should().Be(ResultadoAcessoLocal.EntradaInvalida, terminal.Texto);
        terminal.Texto.Should().Contain("nada foi gravado");
        (await TenantPorCnpjAsync(cnpj)).Should().BeNull("o contribuinte não pode ser criado se a entrada é inválida");
        (await UsuarioPorLoginAsync(login)).Should().BeNull();
        (await AuditoriaDoAtorAsync(ator)).Should().BeEmpty();
        terminal.Texto.Should().NotContain(Senha);
    }

    [SkippableFact]
    public async Task Contribuinte_Desabilitado_E_Recusado_Sem_Criar_Usuario()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        var tenantId = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);
        await CenarioIdentidade.DesabilitarTenantAsync(_fixture, tenantId);

        var terminal = new TerminalFalso(interativo: false);
        (await ExecutarAsync(terminal, ator, EntradaAutomacao(cnpj, login, Senha))).Should().Be(ResultadoAcessoLocal.Recusado);

        terminal.Texto.Should().Contain("DESABILITADO");
        (await UsuarioPorLoginAsync(login)).Should().BeNull();
        (await TenantPorCnpjAsync(cnpj))!.Estado.Should().Be(EstadoProvisionamento.Desabilitado, "a ferramenta não reativa contribuinte");
        (await AuditoriaDoAtorAsync(ator)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Vinculo_Revogado_Nao_E_Reconcedido()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        var tenantId = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);
        (await ExecutarAsync(new TerminalFalso(interativo: false), ator, EntradaAutomacao(cnpj, login, Senha)))
            .Should().Be(ResultadoAcessoLocal.Sucesso);
        var usuario = await UsuarioPorLoginAsync(login);
        await CenarioIdentidade.RevogarAcessoAsync(_fixture, usuario!.Id, tenantId);
        var eventosAntes = (await AuditoriaDoAtorAsync(ator)).Count;

        var terminal = new TerminalFalso(interativo: false);
        (await ExecutarAsync(terminal, ator, EntradaAutomacao(cnpj, login, Senha))).Should().Be(ResultadoAcessoLocal.Recusado);

        terminal.Texto.Should().Contain("REVOGADO");
        (await AuditoriaDoAtorAsync(ator)).Should().HaveCount(eventosAntes);
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var vinculo = await catalogo.UsuarioTenants.SingleAsync(v => v.UsuarioId == usuario.Id && v.TenantId == tenantId);
        vinculo.EstaAtivo.Should().BeFalse();
    }

    [SkippableFact]
    public async Task Codigo_Mfa_Errado_Mantem_Mfa_Inativo_E_Reexecucao_Conclui()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);

        var errado = new TerminalFalso { ResponderCodigoMfa = _ => "000000" }.ResponderOculto(Senha, Senha);
        (await ExecutarAsync(errado, ator, Entrada(cnpj, login) with { AtivarMfa = true })).Should().Be(ResultadoAcessoLocal.Falha);
        errado.PerguntasMfa.Should().Be(3);
        (await UsuarioPorLoginAsync(login))!.MfaHabilitado.Should().BeFalse();
        errado.CodigosRecuperacaoExibidos().Should().BeEmpty();

        // Reexecução: usuário, vínculo e papel já existem; só o MFA é cadastrado (com nova chave).
        var certo = new TerminalFalso();
        (await ExecutarAsync(certo, ator, Entrada(cnpj, login) with { AtivarMfa = true })).Should().Be(ResultadoAcessoLocal.Sucesso, certo.Texto);
        certo.ChaveTotpExibida.Should().NotBe(errado.ChaveTotpExibida);
        (await UsuarioPorLoginAsync(login))!.MfaHabilitado.Should().BeTrue();
        certo.Texto.Should().Contain("senha foi mantida");
    }

    [SkippableFact]
    public async Task Sem_Mfa_E_O_Padrao_E_O_Login_Pede_So_A_Senha()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);

        // Terminal INTERATIVO e nenhuma opção de MFA: o comando não oferece nem exige o cadastro.
        var terminal = new TerminalFalso().ResponderOculto(Senha, Senha);
        (await ExecutarAsync(terminal, ator, Entrada(cnpj, login))).Should().Be(ResultadoAcessoLocal.Sucesso, terminal.Texto);

        terminal.PerguntasMfa.Should().Be(0);
        terminal.ChaveTotpExibida.Should().BeNull("nenhuma chave TOTP é gerada sem --com-mfa");
        terminal.Texto.Should().Contain("opcional");
        // Administrador (vira Administrador da plataforma): o aviso de "alto impacto negado sem MFA" não vale para ele.
        terminal.Texto.Should().Contain("nenhuma operação pede o segundo fator")
            .And.Contain("dispensado para o Administrador da plataforma")
            .And.NotContain("nega só as operações de alto impacto");
        (await UsuarioPorLoginAsync(login))!.MfaHabilitado.Should().BeFalse();
        using var escopo = _fixture.CriarEscopo();
        (await escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>().ExecutarAsync(EmailDe(login), Senha))
            .MfaHabilitado.Should().BeFalse("sem MFA o login termina na senha, sem pedir código");
    }

    [SkippableFact]
    public async Task Remover_Mfa_Desliga_Apaga_Os_Codigos_De_Recuperacao_E_Audita()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);
        var cadastro = new TerminalFalso().ResponderOculto(Senha, Senha);
        (await ExecutarAsync(cadastro, ator, Entrada(cnpj, login) with { AtivarMfa = true })).Should().Be(ResultadoAcessoLocal.Sucesso);
        var recuperacao = cadastro.CodigosRecuperacaoExibidos();
        recuperacao.Should().NotBeEmpty();
        var stampAntes = (await UsuarioPorLoginAsync(login))!.SecurityStamp;

        // Remoção sem terminal interativo (não pede senha nem código).
        var remocao = new TerminalFalso(interativo: false);
        var entrada = new EntradaAcessoLocal(cnpj, login, [PapelConhecido.Administrador]) { RemoverMfa = true };
        (await ExecutarAsync(remocao, ator, entrada)).Should().Be(ResultadoAcessoLocal.Sucesso, remocao.Texto);

        var usuario = await UsuarioPorLoginAsync(login);
        usuario!.MfaHabilitado.Should().BeFalse();
        usuario.SegredoMfaProtegido.Should().BeNull();
        usuario.SecurityStamp.Should().NotBe(stampAntes, "desligar o MFA encerra as sessões abertas");
        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            (await catalogo.CodigosRecuperacaoMfa.CountAsync(c => c.UsuarioId == usuario.Id)).Should().Be(0);
            (await escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>().ExecutarAsync(EmailDe(login), Senha))
                .MfaHabilitado.Should().BeFalse();
            var antigo = async () => await escopo.ServiceProvider.GetRequiredService<GestaoMfa>().ValidarAsync(usuario.Id, recuperacao[0]);
            await antigo.Should().ThrowAsync<MfaInvalidoException>("um código de recuperação antigo não vale mais");
        }

        (await AuditoriaDoAtorAsync(ator)).Should().ContainSingle(e => e.Operacao == "MfaDesativado" && e.RecursoId == usuario.Id.ToString());

        // Idempotente: remover de novo não muda nada nem audita.
        var eventos = (await AuditoriaDoAtorAsync(ator)).Count;
        var deNovo = new TerminalFalso(interativo: false);
        (await ExecutarAsync(deNovo, ator, entrada)).Should().Be(ResultadoAcessoLocal.Sucesso);
        deNovo.Texto.Should().Contain("já está sem MFA");
        (await AuditoriaDoAtorAsync(ator)).Should().HaveCount(eventos);
    }

    [SkippableFact]
    public async Task Reexecucao_Concede_So_Os_Papeis_Que_Faltam_E_Nao_Amplia_Alem_Do_Pedido()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        var tenantId = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, cnpj);
        (await ExecutarAsync(new TerminalFalso(interativo: false), ator, EntradaAutomacao(cnpj, login, Senha) with { Papeis = [PapelConhecido.Leitor] }))
            .Should().Be(ResultadoAcessoLocal.Sucesso);

        var terminal = new TerminalFalso(interativo: false);
        (await ExecutarAsync(terminal, ator, EntradaAutomacao(cnpj, login, Senha) with { Papeis = [PapelConhecido.Leitor, PapelConhecido.Analista] }))
            .Should().Be(ResultadoAcessoLocal.Sucesso, terminal.Texto);

        var concessoes = (await AuditoriaDoAtorAsync(ator)).Where(e => e.Operacao == AcaoAuditoria.RoleAssigned).Select(e => e.Motivo).ToList();
        concessoes.Should().BeEquivalentTo(["Papel Leitor", "Papel Analista"]);
        terminal.Texto.Should().Contain("nega só as operações de alto impacto", "sem MFA e sem ser da plataforma, o aviso continua");

        // Leitor + Analista não administram usuários: a ferramenta concede só o que foi pedido.
        var usuario = await UsuarioPorLoginAsync(login);
        using var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, usuario!.Id, mfaSatisfeito: true);
        var exigir = async () => await escopo.ServiceProvider.GetRequiredService<IServicoAutorizacao>()
            .ExigirAsync(tenantId, Permissoes.AdminUsuarios);
        await exigir.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [SkippableFact]
    public async Task Nome_De_Tenant_Ja_Usado_Por_Outro_Cnpj_E_Recusado_Sem_Gravar()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        (await ExecutarAsync(new TerminalFalso(interativo: false), ator, EntradaAutomacao(cnpj, login, Senha)))
            .Should().Be(ResultadoAcessoLocal.Sucesso);

        // Outro CNPJ e outro usuário pedindo o MESMO nome (em outra caixa): recusado antes de gravar qualquer coisa.
        var (outroCnpj, outroLogin, outroAtor) = NovoCenario();
        var terminal = new TerminalFalso(interativo: false);
        var entrada = EntradaAutomacao(outroCnpj, outroLogin, Senha) with { Tenant = NomeTenantDe(login).ToUpperInvariant() };
        (await ExecutarAsync(terminal, outroAtor, entrada)).Should().Be(ResultadoAcessoLocal.EntradaInvalida, terminal.Texto);

        terminal.Texto.Should().Contain("Já existe um tenant com o nome").And.Contain("nada foi gravado");
        (await TenantPorCnpjAsync(outroCnpj)).Should().BeNull();
        (await UsuarioPorLoginAsync(outroLogin)).Should().BeNull();
        (await AuditoriaDoAtorAsync(outroAtor)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Base_Que_Sobrou_De_Outro_Cnpj_Nao_E_Herdada_E_A_Do_Mesmo_Cnpj_E_Reaproveitada()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (cnpj, login, ator) = NovoCenario();
        var nomeBase = "portalbase_tenant_" + NomeTenantDe(login);
        using var escopoAdmin = _fixture.CriarEscopo();
        var admin = escopoAdmin.ServiceProvider.GetRequiredService<IAdministradorBancoTenant>();

        try
        {
            // Base que "sobrou" (ex.: catálogo perdido) com dados de OUTRO contribuinte: nunca é herdada.
            await admin.CriarBancoSeAusenteAsync(nomeBase);
            await admin.RegistrarProprietarioAsync(nomeBase, Guid.NewGuid(), CenarioMultiTenant.GerarCnpjValido());

            var recusa = new TerminalFalso(interativo: false);
            (await ExecutarAsync(recusa, ator, EntradaAutomacao(cnpj, login, Senha))).Should().Be(ResultadoAcessoLocal.EntradaInvalida, recusa.Texto);
            recusa.Texto.Should().Contain("outro contribuinte").And.Contain("nada foi gravado");
            (await TenantPorCnpjAsync(cnpj)).Should().BeNull();
            (await UsuarioPorLoginAsync(login)).Should().BeNull();
            (await AuditoriaDoAtorAsync(ator)).Should().BeEmpty();

            // A mesma base, agora marcada como do MESMO CNPJ (o contribuinte recadastrado): reaproveitada e auditada.
            await admin.RegistrarProprietarioAsync(nomeBase, Guid.NewGuid(), cnpj);
            var reuso = new TerminalFalso(interativo: false);
            (await ExecutarAsync(reuso, ator, EntradaAutomacao(cnpj, login, Senha))).Should().Be(ResultadoAcessoLocal.Sucesso, reuso.Texto);
            reuso.Texto.Should().Contain("será reaproveitada");
            var tenant = await TenantPorCnpjAsync(cnpj);
            tenant!.NomeBaseFisica.Should().Be(nomeBase);
            tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
            (await admin.ObterProprietarioAsync(nomeBase)).TenantId.Should().Be(tenant.Id);
            (await AuditoriaDoAtorAsync(ator)).Should().Contain(e => e.Operacao == AcaoAuditoria.TenantDatabaseReused);
        }
        finally
        {
            if (await TenantPorCnpjAsync(cnpj) is null)
            {
                await admin.RemoverBancoAsync(nomeBase); // sem tenant no catálogo, a limpeza da fixture não a acharia
            }
        }
    }

    // ---------------- helpers ----------------

    private static (string Cnpj, string Login, string Ator) NovoCenario()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        return (CenarioMultiTenant.GerarCnpjValido(), $"local.{sufixo}", $"acesso-local:teste-{sufixo}");
    }

    /// <summary>Executa o comando e, em TODA execução, prova que nenhuma senha usada nos testes chegou à saída.</summary>
    private async Task<ResultadoAcessoLocal> ExecutarAsync(TerminalFalso terminal, string ator, EntradaAcessoLocal entrada)
    {
        var criador = new CriadorAcessoLocal(_fixture.Servicos.GetRequiredService<IServiceScopeFactory>(), terminal, ator);
        var resultado = await criador.ExecutarAsync(entrada);
        terminal.Texto.Should().NotContain(Senha).And.NotContain(OutraSenha).And.NotContain("curta");
        return resultado;
    }

    private static EntradaAcessoLocal Entrada(string cnpj, string login) => new(cnpj, login, [PapelConhecido.Administrador])
    {
        Tenant = NomeTenantDe(login),
        RazaoSocial = "Contribuinte Local de Teste Ltda",
        Nome = "Usuário Local",
        Email = EmailDe(login),
    };

    /// <summary>Nome de tenant único por cenário, em minúsculas (a ferramenta normaliza para MAIÚSCULO).</summary>
    private static string NomeTenantDe(string login) => "local_" + login["local.".Length..];

    // E-mail do usuário criado pelo comando: é o identificador do login.
    private static string EmailDe(string login) => $"{login}@exemplo.com";

    private static EntradaAcessoLocal EntradaAutomacao(string cnpj, string login, string senha)
        => Entrada(cnpj, login) with { AtivarMfa = false, Senha = senha };

    private async Task<Tenant?> TenantPorCnpjAsync(string cnpj)
    {
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var valor = Cnpj.Criar(cnpj);
        var tenant = await catalogo.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Cnpj == valor);
        if (tenant is not null)
        {
            _fixture.RegistrarTenant(tenant.Id); // limpeza da base tenant_* (quando provisionada)
        }

        return tenant;
    }

    private async Task<Usuario?> UsuarioPorLoginAsync(string login)
    {
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        return await catalogo.Usuarios.AsNoTracking().SingleOrDefaultAsync(u => u.Login == login);
    }

    private async Task<List<RegistroAuditoria>> AuditoriaDoAtorAsync(string ator)
    {
        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        return await catalogo.RegistrosAuditoria.AsNoTracking().Where(r => r.PrincipalId == ator).OrderBy(r => r.Id).ToListAsync();
    }
}
