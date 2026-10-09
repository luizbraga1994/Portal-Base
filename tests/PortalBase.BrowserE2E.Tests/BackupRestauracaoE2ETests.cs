using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MySql.Data.MySqlClient;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Backup;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Multitenancy;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Arquivos;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// BACKUP E RESTAURAÇÃO pela tela, no organismo integrado com mysqldump/mysql REAIS: o Administrador da plataforma copia
/// o contribuinte A agora, testa a restauração (base temporária, apagada), o dado de A muda depois do backup, e a
/// restauração pela tela traz o dado de volta — lido pela credencial de RUNTIME (grants preservados), com o contribuinte
/// ativo de novo, a base anterior guardada à parte e a auditoria registrada. O contribuinte B não é tocado. Entradas
/// hostis (backup de A em B, nome de confirmação errado, alvo inválido, usuário sem perfil de plataforma) são recusadas
/// no SERVIDOR. Sem MySQL: BLOCKED; sem mysqldump/mysql no host: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class BackupRestauracaoE2ETests
{
    private static readonly LocatorWaitForOptions Espera = new() { Timeout = 120_000 };
    private const string Justificativa = "Importação errada de setembro: voltar ao backup feito antes dela.";

    private readonly AmbienteE2EIntegrado _ambiente;

    public BackupRestauracaoE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    private void ExigirBackupFisico()
    {
        _ambiente.Exigir();
        Skip.IfNot(_ambiente.BackupFisicoDisponivel, "BLOCKED (infra): mysqldump/mysql ausentes no host — backup físico indisponível.");
    }

    [SkippableFact]
    public async Task Administrador_Faz_Backup_Testa_E_Restaura_Um_Contribuinte_Pela_Tela()
    {
        ExigirBackupFisico();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var p = sessao.Pagina;
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, plataforma);
        await SessaoBrowserE2E.AguardarUrlAsync(p, "**/conta/selecionar**");
        await SessaoBrowserE2E.SelecionarContribuinteAsync(p, cenario.A.TenantId);
        await p.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });

        // 1. Backup de A agora.
        await IrAsync(p, "/operacoes/backups");
        await p.Locator("#situacao-backup li").First.WaitForAsync(Espera);
        (await p.Locator("#situacao-backup").InnerTextAsync()).Should().Contain("(mysqldump/mysql): disponível");
        await p.Locator($"#backup-alvo option[value='{cenario.A.TenantId}']").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
        await p.Locator("#backup-alvo").SelectOptionAsync(cenario.A.TenantId.ToString());
        await p.Locator("#backup-retencao").SelectOptionAsync("Semanal");
        await p.Locator("#btn-fazer-backup").ClickAsync();
        await p.Locator(".modal .btn-primary:has-text('Fazer backup')").ClickAsync();
        await p.Locator("#resultado-backup:has-text('Backup concluído')").WaitForAsync(Espera);

        // O backup do contribuinte copia a BASE e os ARQUIVOS dele (BKP-005): duas linhas; a da base é a que restaura.
        var linhaArquivosA = p.Locator("#tabela-backups tbody tr", new PageLocatorOptions { HasText = $"Arquivos — {cenario.A.Nome}" }).First;
        await linhaArquivosA.WaitForAsync(Espera);
        (await linhaArquivosA.InnerTextAsync()).Should().Contain("Concluído");
        (await linhaArquivosA.Locator(".btn-restaurar").CountAsync()).Should().Be(0, "o pacote de arquivos não é restaurado pela tela (runbook)");
        var linhaA = p.Locator("#tabela-backups tbody tr", new PageLocatorOptions { HasText = cenario.A.Nome, Has = p.Locator(".btn-restaurar") }).First;
        await linhaA.WaitForAsync(Espera);
        (await linhaA.InnerTextAsync()).Should().Contain("Concluído").And.NotContain("portalbase_tenant", "a tela não mostra a base física");
        var backupId = await linhaA.Locator(".btn-restaurar").GetAttributeAsync("data-backup");
        backupId.Should().NotBeNullOrWhiteSpace();

        // 2. Testar a restauração (base temporária, apagada; produção intocada).
        await linhaA.Locator(".btn-testar-restauracao").ClickAsync();
        await p.Locator(".modal .btn-primary:has-text('Testar')").ClickAsync();
        await p.Locator("#resultado-restauracao:has-text('Restauração testada')").WaitForAsync(Espera);
        var baseA = await BaseFisicaAsync(cenario.A.TenantId);
        (await BasesAsync(Escapar(baseA) + "\\_tst%")).Should().BeEmpty("a base temporária do teste é apagada");

        // 3. Depois do backup, o dado de A muda (e o de B também — B não será restaurado).
        (await ApagarArquivoSementeAsync(cenario.A)).Should().Be(1);
        (await ApagarArquivoSementeAsync(cenario.B)).Should().Be(1);
        (await LerArquivoSementeAsync(cenario.A)).Should().BeNull();

        // 4. Restaurar A pela tela: justificativa + nome digitado (errado ⇒ botão desabilitado).
        await IrAsync(p, "/operacoes/backups");
        await p.Locator($"#tabela-backups .btn-restaurar[data-backup='{backupId}']").ClickAsync();
        await p.Locator("#restauracao-contribuinte").WaitForAsync(Espera);
        await p.Locator("#restaurar-justificativa").FillAsync(Justificativa);
        await p.Locator("#restaurar-confirmacao").FillAsync(cenario.B.Nome);
        (await p.Locator("#btn-restaurar-contribuinte").IsDisabledAsync()).Should().BeTrue("o nome digitado não confere");
        await p.Locator("#restaurar-confirmacao").FillAsync(cenario.A.Nome);
        await InteracaoBlazorE2E.RepetirAteAsync(
            () => p.Locator("#btn-restaurar-contribuinte").ClickAsync(new LocatorClickOptions { Timeout = 5_000 }),
            p.Locator(".modal .btn-danger:has-text('Restaurar')"));
        await p.Locator(".modal .btn-danger:has-text('Restaurar')").ClickAsync();
        await p.Locator("#resultado-restauracao:has-text('Contribuinte restaurado')").WaitForAsync(Espera);
        (await p.Locator("#resultado-restauracao").InnerTextAsync()).Should().NotContain("portalbase_tenant").And.NotContain("_ant");

        // 5. O dado de A voltou (lido pela credencial de RUNTIME ⇒ grants e contribuinte ativo); B continua como estava.
        (await LerArquivoSementeAsync(cenario.A)).Should().NotBeNull("a restauração trouxe de volta o dado apagado depois do backup");
        (await LerArquivoSementeAsync(cenario.B)).Should().BeNull("o contribuinte B não foi restaurado");

        using var escopo = _ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var tenantA = await catalogo.Tenants.AsNoTracking().SingleAsync(t => t.Id == cenario.A.TenantId);
        tenantA.Estado.Should().Be(EstadoProvisionamento.Ativo);
        var preservadas = await BasesAsync(Escapar(tenantA.NomeBaseFisica) + "\\_ant%");
        preservadas.Should().ContainSingle("a base anterior fica guardada à parte (nunca apagada pela restauração)");
        (await BasesAsync(Escapar(tenantA.NomeBaseFisica) + "\\_rst%")).Should().BeEmpty("a base de preparo vazia é removida");

        var auditoria = await catalogo.RegistrosAuditoria.AsNoTracking()
            .Where(r => r.Operacao == AcaoAuditoria.RestoreAppliedToTenant && r.Tenant == cenario.A.TenantId.ToString())
            .SingleAsync();
        auditoria.PrincipalId.Should().Be(plataforma.UsuarioId.ToString(), "quem restaurou vem da sessão");
        auditoria.Motivo.Should().Be(Justificativa);
    }

    [SkippableFact]
    public async Task Entradas_Hostis_Sao_Recusadas_No_Servidor()
    {
        ExigirBackupFisico();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);

        using (var escopo = _ambiente.CriarEscopoSuporte())
        {
            var dados = Criar(escopo, plataforma.UsuarioId.ToString());

            (await dados.FazerBackupAsync("nao-e-um-id", "Diario")).Estado.Should().Be(EstadoAcaoUi.Invalido);
            (await dados.FazerBackupAsync("todos", "Anual")).Estado.Should().Be(EstadoAcaoUi.Invalido);
            (await dados.FazerBackupAsync(Guid.NewGuid().ToString(), "Diario")).Estado
                .Should().Be(EstadoAcaoUi.Invalido, "contribuinte fora do catálogo não é copiado");

            var feito = await dados.FazerBackupAsync(cenario.A.TenantId.ToString(), "Diario");
            feito.Sucesso.Should().BeTrue(feito.Mensagem);
            var (itens, _) = await dados.ListarRecentesAsync();
            var backupDeA = itens.First(i => i.TenantId == cenario.A.TenantId).Id;

            // Backup de A aplicado em B: negado e auditado; B intacto.
            var cruzado = await dados.RestaurarContribuinteAsync(backupDeA, cenario.B.TenantId, Justificativa, cenario.B.Nome);
            cruzado.Estado.Should().Be(EstadoAcaoUi.Invalido);
            cruzado.Titulo.Should().Be("Backup de outro contribuinte");
            (await LerArquivoSementeAsync(cenario.B)).Should().NotBeNull();

            // Nome de confirmação errado / justificativa curta: nada acontece.
            (await dados.RestaurarContribuinteAsync(backupDeA, cenario.A.TenantId, Justificativa, cenario.B.Nome)).Estado
                .Should().Be(EstadoAcaoUi.Invalido);
            (await dados.RestaurarContribuinteAsync(backupDeA, cenario.A.TenantId, "curta", cenario.A.Nome)).Estado
                .Should().Be(EstadoAcaoUi.Invalido);

            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            (await catalogo.RegistrosAuditoria.AsNoTracking()
                .AnyAsync(r => r.Operacao == AcaoAuditoria.RestoreCrossTenantDenied && r.Tenant == cenario.B.TenantId.ToString()))
                .Should().BeTrue();
            (await catalogo.RegistrosAuditoria.AsNoTracking()
                .AnyAsync(r => r.Operacao == AcaoAuditoria.RestoreAppliedToTenant
                    && (r.Tenant == cenario.A.TenantId.ToString() || r.Tenant == cenario.B.TenantId.ToString())))
                .Should().BeFalse("nenhuma restauração foi aplicada");
        }

        using (var escopo = _ambiente.CriarEscopoSuporte())
        {
            // Administrador do CONTRIBUINTE (sem perfil de plataforma): o servidor nega backup e restauração.
            var dados = Criar(escopo, cenario.A.Credencial.UsuarioId.ToString());
            (await dados.FazerBackupAsync(cenario.A.TenantId.ToString(), "Diario")).Estado.Should().Be(EstadoAcaoUi.SemPermissao);
            (await dados.TestarRestauracaoAsync(Guid.NewGuid())).Estado.Should().Be(EstadoAcaoUi.SemPermissao);
            (await dados.RestaurarContribuinteAsync(Guid.NewGuid(), cenario.A.TenantId, Justificativa, cenario.A.Nome)).Estado
                .Should().Be(EstadoAcaoUi.SemPermissao);
        }
    }

    private static DadosBackups Criar(IServiceScope escopo, string principal)
    {
        var sp = escopo.ServiceProvider;
        sp.GetRequiredService<IContextoSegurancaMutavel>().Definir(principal, mfaSatisfeito: true);
        return new DadosBackups(
            sp.GetRequiredService<ConsultaBackupsAutorizada>(), sp.GetRequiredService<BackupSobDemanda>(),
            sp.GetRequiredService<RestauracaoPlataforma>(), sp.GetRequiredService<CadastroContribuinte>());
    }

    // Lê/apaga o arquivo semeado do contribuinte pela credencial de RUNTIME (o mesmo caminho do portal).
    private async Task<ArquivoImportacao?> LerArquivoSementeAsync(ContribuinteE2E contribuinte)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        var contexto = await escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>()
            .ResolverAsync(contribuinte.UsuarioId.ToString(), contribuinte.TenantId);
        await using var db = await escopo.ServiceProvider.GetRequiredService<IFabricaContextoTenant>().CriarAsync(contexto);
        return await db.ArquivosImportacao.AsNoTracking().FirstOrDefaultAsync(a => a.Id == contribuinte.ArquivoSementeId);
    }

    private async Task<int> ApagarArquivoSementeAsync(ContribuinteE2E contribuinte)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        var contexto = await escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>()
            .ResolverAsync(contribuinte.UsuarioId.ToString(), contribuinte.TenantId);
        await using var db = await escopo.ServiceProvider.GetRequiredService<IFabricaContextoTenant>().CriarAsync(contexto);
        return await db.ArquivosImportacao.Where(a => a.Id == contribuinte.ArquivoSementeId).ExecuteDeleteAsync();
    }

    private async Task<string> BaseFisicaAsync(Guid tenantId)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId).Select(t => t.NomeBaseFisica).SingleAsync();
    }

    private static string Escapar(string nomeBase) => nomeBase.Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<IReadOnlyList<string>> BasesAsync(string padraoLike)
    {
        await using var conexao = new MySqlConnection(Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN"));
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME LIKE @padrao";
        comando.Parameters.AddWithValue("@padrao", padraoLike);
        var nomes = new List<string>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            nomes.Add(leitor.GetString(0));
        }

        return nomes;
    }

    private static Task IrAsync(IPage pagina, string rota)
        => InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync(rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
}
