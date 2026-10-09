using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Prova de AUTORIZAÇÃO e ISOLAMENTO por identidade contra MySQL real (seções 33-37). Cobre
/// RBAC (papel insuficiente), IDOR de tenant (manipulação de tenantId), step-up de MFA,
/// revogação, tenant desabilitado, auto-escalonamento e concorrência. Pulado sem
/// <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class AuthorizationIsolationTests
{
    private readonly FixtureMultiTenant _fixture;

    public AuthorizationIsolationTests(FixtureMultiTenant fixture) => _fixture = fixture;

    private async ValueTask ExigirAsync(Guid usuarioId, bool mfa, Guid tenantId, string permissao)
    {
        using var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, usuarioId, mfa);
        var autorizacao = escopo.ServiceProvider.GetRequiredService<IServicoAutorizacao>();
        await autorizacao.ExigirAsync(tenantId, permissao);
    }

    // §33.2 — Leitor tentando operação de Analista → negado.
    [SkippableFact]
    public async Task Leitor_Nao_Executa_Operacao_De_Analista()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, a, PapelConhecido.Leitor);

        var acao = async () => await ExigirAsync(usuario, false, a, Permissoes.DadosImportar);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    // §33.3 — Leitor tentando operação de alto impacto → negado (nem com MFA).
    [SkippableFact]
    public async Task Leitor_Nao_Exclui_Dados_Nem_Com_Mfa()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, a, PapelConhecido.Leitor);

        var acao = async () => await ExigirAsync(usuario, true, a, Permissoes.DadosExcluir);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    // §33.4 / §33.5 — operação de ALTO IMPACTO (dados.excluir): o Administrador só com MFA; o Analista nunca (sem a permissão).
    [SkippableFact]
    public async Task Excluir_Dados_Exige_Mfa_E_Analista_Nao_Exclui()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (admin, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, admin, a, PapelConhecido.Administrador);
        var (analista, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, analista, a, PapelConhecido.Analista);

        var adminSemMfa = async () => await ExigirAsync(admin, false, a, Permissoes.DadosExcluir);
        await adminSemMfa.Should().ThrowAsync<MfaRequeridoException>();
        var adminComMfa = async () => await ExigirAsync(admin, true, a, Permissoes.DadosExcluir);
        await adminComMfa.Should().NotThrowAsync();

        var analistaComMfa = async () => await ExigirAsync(analista, true, a, Permissoes.DadosExcluir);
        await analistaComMfa.Should().ThrowAsync<AutorizacaoNegadaException>("o Analista não tem dados.excluir");
        foreach (var permissao in new[] { Permissoes.DadosCadastrar, Permissoes.DadosEditar, Permissoes.DadosImportar })
        {
            var permitido = async () => await ExigirAsync(analista, false, a, permissao);
            await permitido.Should().NotThrowAsync(because: $"o Analista tem {permissao} e ela não exige MFA");
        }
    }

    // §33.7 / §35 — IDOR: usuário de A tenta B manipulando o tenantId → negado.
    [SkippableFact]
    public async Task Usuario_De_A_Nao_Acessa_B_Manipulando_TenantId()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, a, PapelConhecido.Analista); // só A

        var acao = async () => await ExigirAsync(usuario, false, b, Permissoes.DadosVisualizar);
        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
    }

    // §33.8 — usuário com acesso a A e B alterna para B → permitido.
    [SkippableFact]
    public async Task Usuario_Com_A_E_B_Acessa_B()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, a, PapelConhecido.Analista);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, b, PapelConhecido.Analista);

        var acao = async () => await ExigirAsync(usuario, false, b, Permissoes.DadosVisualizar);
        await acao.Should().NotThrowAsync();
    }

    // §33.9 — perda de acesso a B → negado.
    [SkippableFact]
    public async Task Perda_De_Acesso_Nega_B()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, b, PapelConhecido.Analista);
        await CenarioIdentidade.RevogarAcessoAsync(_fixture, usuario, b);

        var acao = async () => await ExigirAsync(usuario, false, b, Permissoes.DadosVisualizar);
        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
    }

    // §33.10 — tenant desabilitado → negado.
    [SkippableFact]
    public async Task Tenant_Desabilitado_Nega()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuario, a, PapelConhecido.Analista);
        await CenarioIdentidade.DesabilitarTenantAsync(_fixture, a);

        var acao = async () => await ExigirAsync(usuario, false, a, Permissoes.DadosVisualizar);
        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
    }

    // §33.11-13 — auto-concessão de papel bloqueada (fluxo real de admin).
    [SkippableFact]
    public async Task Auto_Concessao_De_Papel_Bloqueada()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (admin, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, admin, a, PapelConhecido.Administrador);

        using var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, admin, mfaSatisfeito: false);
        var gestao = escopo.ServiceProvider.GetRequiredService<GestaoAcesso>();

        var acao = async () => await gestao.AtribuirPapelAsync(a, admin, PapelConhecido.Analista);
        await acao.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
    }

    // §33.1/4/6 — login real pelo E-MAIL: autentica com a senha correta e recusa a errada (hash PBKDF2). O catálogo
    // compara texto SENSÍVEL a caixa (utf8mb4_0900_as_cs): o e-mail em outra caixa só acha o usuário porque gravação e
    // busca usam a forma canônica. O nome de usuário interno não autentica.
    [SkippableFact]
    public async Task Login_Real_Autentica_E_Recusa_Senha_Errada()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (_, login) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        var email = login + "@exemplo.com";

        using var escopo = _fixture.CriarEscopo();
        var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();

        var ok = await autenticar.ExecutarAsync(email, "SenhaForte#2026");
        ok.Login.Should().Be(login);
        (await autenticar.ExecutarAsync(" " + email.ToUpperInvariant(), "SenhaForte#2026")).UsuarioId.Should().Be(ok.UsuarioId);

        var errada = async () => await autenticar.ExecutarAsync(email, "errada");
        await errada.Should().ThrowAsync<CredenciaisInvalidasException>();

        var peloNomeDeUsuario = async () => await autenticar.ExecutarAsync(login, "SenhaForte#2026");
        await peloNomeDeUsuario.Should().ThrowAsync<CredenciaisInvalidasException>();
    }

    // §18 — MFA real: o segredo é guardado PROTEGIDO (AES-GCM) e decifra de volta ao original.
    [SkippableFact]
    public async Task Mfa_Real_Guarda_Segredo_Protegido_E_Reversivel()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");

        SegredoMfaProvisionamento provisionamento;
        using (var escopo = _fixture.CriarEscopo())
        {
            var mfa = escopo.ServiceProvider.GetRequiredService<GestaoMfa>();
            provisionamento = await mfa.IniciarAtivacaoAsync(usuario);
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var protetor = escopo.ServiceProvider.GetRequiredService<IProtetorSegredoMfa>();
            var persistido = await catalogo.Usuarios.AsNoTracking().FirstAsync(u => u.Id == usuario);

            persistido.SegredoMfaProtegido.Should().NotBeNullOrEmpty();
            persistido.SegredoMfaProtegido.Should().NotContain(provisionamento.SegredoBase32); // não em claro
            (await protetor.DesprotegerAsync(persistido.SegredoMfaProtegido!)).Should().Be(provisionamento.SegredoBase32);
        }
    }

    // §37 — concorrência: dois vínculos do mesmo (usuário, tenant) → a restrição única impede duplicidade.
    [SkippableFact]
    public async Task Concorrencia_Nao_Duplica_Vinculo()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (usuario, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");

        async Task Conceder()
        {
            using var escopo = _fixture.CriarEscopo();
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            catalogo.UsuarioTenants.Add(UsuarioTenant.Conceder(usuario, a, DateTimeOffset.UtcNow));
            await catalogo.SaveChangesAsync();
        }

        var primeira = Conceder();
        var segunda = Conceder();
        var acao = async () => await Task.WhenAll(primeira, segunda);
        await acao.Should().ThrowAsync<DbUpdateException>(); // ux_usuario_tenant impede o segundo

        using var verificacao = _fixture.CriarEscopo();
        var db = verificacao.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        (await db.UsuarioTenants.CountAsync(v => v.UsuarioId == usuario && v.TenantId == a)).Should().Be(1);
    }
}
