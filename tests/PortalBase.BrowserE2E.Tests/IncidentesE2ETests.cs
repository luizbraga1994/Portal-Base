using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Incidentes;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Incidentes;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Gestão de INCIDENTES pela tela, no organismo integrado: o Administrador da plataforma registra um incidente com o
/// contribuinte afetado, inicia a análise, registra a contenção, classifica o risco ao titular (ele fica responsável) e
/// encerra — a linha do tempo mostra cada etapa com o nome de quem fez, e tudo fica no catálogo. Entradas hostis
/// (contribuinte fora da lista, tipo numérico, classificação "não avaliado", encerrar direto da detecção, usuário sem
/// a permissão de plataforma) são recusadas no SERVIDOR. Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class IncidentesE2ETests
{
    private static readonly LocatorWaitForOptions Espera = new() { Timeout = 60_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public IncidentesE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Administrador_Registra_Trata_E_Encerra_Um_Incidente_Pela_Tela()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var p = sessao.Pagina;
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, plataforma);
        await SessaoBrowserE2E.AguardarUrlAsync(p, "**/conta/selecionar**");
        await SessaoBrowserE2E.SelecionarContribuinteAsync(p, cenario.A.TenantId);
        await p.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });

        await IrAsync(p, "/auditoria/incidentes");
        await p.Locator("#btn-novo-incidente").ClickAsync();
        var titulo = "Acesso indevido E2E " + Guid.NewGuid().ToString("N")[..6];
        await p.Locator("#inc-contribuinte option[value='" + cenario.A.TenantId + "']").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
        await p.Locator("#inc-severidade").SelectOptionAsync("Alto");
        await p.Locator("#inc-contribuinte").SelectOptionAsync(cenario.A.TenantId.ToString());
        await p.Locator("#inc-titulo").FillAsync(titulo);
        await p.Locator("#inc-descricao").FillAsync("Tentativas repetidas de login vindas de um IP desconhecido.");
        await p.Locator("#btn-registrar-incidente").ClickAsync();
        await p.WaitForURLAsync("**/auditoria/incidentes/*", new PageWaitForURLOptions { Timeout = 30_000 });
        await p.Locator("#acoes-incidente").WaitForAsync(Espera);

        await ExecutarAcaoAsync(p, AcaoIncidenteUi.IniciarAnalise, "Logs do proxy coletados.", "Análise iniciada");
        await ExecutarAcaoAsync(p, AcaoIncidenteUi.RegistrarContencao, "IP bloqueado no firewall e senhas redefinidas.", "Contenção registrada");

        await p.Locator("#acao-incidente").SelectOptionAsync(((int)AcaoIncidenteUi.ClassificarRisco).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await p.Locator("#classificacao-risco").SelectOptionAsync("SemRiscoRelevante");
        await p.Locator("#chk-dados-pessoais").CheckAsync();
        await p.Locator("#texto-acao").FillAsync("Nenhum dado foi lido: as tentativas falharam no segundo fator.");
        await p.Locator("#btn-executar-acao").ClickAsync();
        await p.Locator("#resultado-incidente:has-text('Risco classificado')").WaitForAsync(Espera);

        await ExecutarAcaoAsync(p, AcaoIncidenteUi.Encerrar, "Contido sem vazamento; monitoramento reforçado.", "Incidente encerrado");
        (await p.Locator("#acoes-incidente").CountAsync()).Should().Be(0, "encerrado é imutável: não há mais ações");

        var linhas = p.Locator("#timeline-incidente tbody tr");
        (await linhas.CountAsync()).Should().Be(5);
        var timeline = await p.Locator("#timeline-incidente").InnerTextAsync();
        timeline.Should().Contain("Detecção").And.Contain("Análise").And.Contain("Contenção").And.Contain("Classificação").And.Contain("Encerramento")
            .And.Contain("IP bloqueado no firewall");
        (await p.Locator("#responsavel-incidente").InnerTextAsync()).Should().NotContain(plataforma.UsuarioId.ToString(), "mostra o nome, não o identificador");

        using var escopo = _ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var incidente = await catalogo.IncidentesSeguranca.AsNoTracking().SingleAsync(i => i.Titulo == titulo);
        incidente.Status.Should().Be(StatusIncidente.Encerrado);
        incidente.TenantAfetadoId.Should().Be(cenario.A.TenantId);
        incidente.Tipo.Should().Be(TipoIncidente.ComDadosPessoais, "a classificação marcou dados pessoais");
        incidente.ClassificacaoRisco.Should().Be(ClassificacaoRiscoTitular.SemRiscoRelevante);
        incidente.ResponsavelId.Should().Be(plataforma.UsuarioId.ToString(), "o responsável é quem decidiu (da sessão), nunca um id do cliente");
    }

    [SkippableFact]
    public async Task Entradas_Hostis_Sao_Recusadas_No_Servidor()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);

        using (var escopo = _ambiente.CriarEscopoSuporte())
        {
            var acoes = Criar(escopo, plataforma.UsuarioId.ToString());
            var titulo = "Hostil " + Guid.NewGuid().ToString("N")[..6];

            var foraDaLista = await acoes.RegistrarAsync(new NovoIncidenteVm("Tecnico", "Alto", titulo, "x", null, null, Guid.NewGuid()));
            foraDaLista.Estado.Should().Be(EstadoAcaoUi.Invalido);
            foraDaLista.Mensagem.Should().Contain("Contribuinte afetado inválido");

            (await acoes.RegistrarAsync(new NovoIncidenteVm("2", "Alto", titulo, "x", null, null, null))).Estado
                .Should().Be(EstadoAcaoUi.Invalido, "valor numérico não é aceito no lugar do nome do tipo");
            (await acoes.RegistrarAsync(new NovoIncidenteVm("Tecnico", "Gravissimo", titulo, "x", null, null, null))).Estado
                .Should().Be(EstadoAcaoUi.Invalido);

            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            (await catalogo.IncidentesSeguranca.AsNoTracking().AnyAsync(i => i.Titulo == titulo)).Should().BeFalse("nada é gravado com entrada inválida");

            var criado = await acoes.RegistrarAsync(new NovoIncidenteVm("Tecnico", "Baixo", titulo, "Falha de configuração.", null, null, cenario.A.TenantId));
            criado.Sucesso.Should().BeTrue();
            var id = criado.Id!.Value;

            (await acoes.ExecutarAsync(id, AcaoIncidenteUi.ClassificarRisco, "justificativa", classificacao: "NaoAvaliado")).Estado
                .Should().Be(EstadoAcaoUi.Invalido, "a classificação exige uma decisão explícita");
            var encerrarDireto = await acoes.ExecutarAsync(id, AcaoIncidenteUi.Encerrar, "resumo");
            encerrarDireto.Estado.Should().Be(EstadoAcaoUi.Conflito);
            encerrarDireto.Mensagem.Should().Contain("análise/contenção");
            (await acoes.ExecutarAsync(id, (AcaoIncidenteUi)99, "x")).Estado.Should().Be(EstadoAcaoUi.Invalido);
        }

        using (var escopo = _ambiente.CriarEscopoSuporte())
        {
            // Administrador do CONTRIBUINTE (sem perfil de plataforma): o servidor nega, nada é gravado.
            var acoes = Criar(escopo, cenario.A.Credencial.UsuarioId.ToString());
            var negado = await acoes.RegistrarAsync(new NovoIncidenteVm("Tecnico", "Alto", "Sem permissão", "x", null, null, null));
            negado.Estado.Should().Be(EstadoAcaoUi.SemPermissao);
        }
    }

    private static DadosAcoesIncidentes Criar(IServiceScope escopo, string principal)
    {
        var sp = escopo.ServiceProvider;
        sp.GetRequiredService<IContextoSegurancaMutavel>().Definir(principal, mfaSatisfeito: true);
        return new DadosAcoesIncidentes(
            sp.GetRequiredService<ServicoIncidentes>(), sp.GetRequiredService<ConsultaIncidentesAutorizada>(),
            sp.GetRequiredService<CadastroContribuinte>(), sp.GetRequiredService<IConsultaNomesAtores>(), sp.GetRequiredService<IContextoSeguranca>());
    }

    private static async Task ExecutarAcaoAsync(IPage p, AcaoIncidenteUi acao, string texto, string esperado)
    {
        await p.Locator("#acao-incidente").SelectOptionAsync(((int)acao).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await p.Locator("#texto-acao").FillAsync(texto);
        await p.Locator("#btn-executar-acao").ClickAsync();
        await p.Locator($"#resultado-incidente:has-text('{esperado}')").WaitForAsync(Espera);
    }

    private static Task IrAsync(IPage pagina, string rota)
        => InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync(rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
}
