using FluentAssertions;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Incidentes;
using PortalBase.Web.Ui.Dados;
using PortalBase.Web.Ui.Navegacao;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Frontend Administração e Operações (Prompt 40): badges seguros (cor + texto) de incidentes e operações,
/// estado honesto de telemetria de worker (ausência ≠ saudável) e promoção do menu. Testes puros — sem
/// MySQL/Docker.
/// </summary>
public sealed class AdminOperacoesUiTests
{
    // ---- Incidentes (severidade/status/risco — §7) ----

    [Theory]
    [InlineData(SeveridadeIncidente.Critico, "bad")]
    [InlineData(SeveridadeIncidente.Alto, "bad")]
    [InlineData(SeveridadeIncidente.Medio, "warn")]
    [InlineData(SeveridadeIncidente.Baixo, "info")]
    [InlineData(SeveridadeIncidente.Informativo, "neutral")]
    public void Badge_Severidade(SeveridadeIncidente s, string esperado)
        => AdminOpsUi.BadgeSeveridade(s).Should().Be(esperado);

    [Theory]
    [InlineData(StatusIncidente.Encerrado, "ok")]
    [InlineData(StatusIncidente.Contido, "primary")]
    [InlineData(StatusIncidente.EmAnalise, "warn")]
    [InlineData(StatusIncidente.Detectado, "bad")]
    public void Badge_StatusIncidente(StatusIncidente s, string esperado)
        => AdminOpsUi.BadgeStatusIncidente(s).Should().Be(esperado);

    [Theory]
    [InlineData(ClassificacaoRiscoTitular.ComRiscoOuDanoRelevante, "bad")]
    [InlineData(ClassificacaoRiscoTitular.SemRiscoRelevante, "ok")]
    [InlineData(ClassificacaoRiscoTitular.NaoAvaliado, "warn")]
    public void Badge_Risco(ClassificacaoRiscoTitular c, string esperado)
        => AdminOpsUi.BadgeRisco(c).Should().Be(esperado);

    [Fact]
    public void Rotulos_De_Incidente_Sao_Legiveis()
    {
        AdminOpsUi.RotuloTipoIncidente(TipoIncidente.ComDadosPessoais).Should().Be("Com dados pessoais");
        AdminOpsUi.RotuloStatusIncidente(StatusIncidente.EmAnalise).Should().Be("Em análise");
        AdminOpsUi.RotuloRisco(ClassificacaoRiscoTitular.NaoAvaliado).Should().Be("Não avaliado");
    }

    [Theory]
    [InlineData("Detectado", true)]
    [InlineData("EmAnalise", true)]
    [InlineData("Contido", true)]
    [InlineData("Encerrado", false)]
    public void Incidente_Vm_Reconhece_Em_Aberto(string status, bool emAberto)
    {
        var vm = new RegistroIncidenteVm(Guid.NewGuid(), "INC-1", "Técnico", "Alto", "bad", status, "warn",
            "Título", null, "Não avaliado", "warn", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        vm.EmAberto.Should().Be(emAberto);
    }

    // ---- Operações: estado de worker/integração (§8) ----

    [Theory]
    [InlineData(false, true, "Estagnado", "bad")]
    [InlineData(true, false, "Ativo", "ok")]
    [InlineData(false, false, "Inativo", "neutral")]
    public void Estado_Worker(bool ativo, bool estagnado, string rotulo, string badge)
    {
        var (r, b) = AdminOpsUi.EstadoWorker(ativo, estagnado);
        r.Should().Be(rotulo);
        b.Should().Be(badge);
    }

    [Theory]
    [InlineData(true, "Degradada", "bad")]
    [InlineData(false, "Saudável", "ok")]
    public void Estado_Integracao(bool degradada, string rotulo, string badge)
    {
        var (r, b) = AdminOpsUi.EstadoIntegracao(degradada);
        r.Should().Be(rotulo);
        b.Should().Be(badge);
    }

    // ---- Ausência de telemetria NUNCA é "saudável" (§8, limitação entre-processos) ----

    [Fact]
    public void Painel_Sem_Workers_Indica_Ausencia_De_Telemetria_Nao_Saude()
    {
        var vazio = new PainelOperacoesVm([], [], 0, 0, DateTimeOffset.UtcNow);
        vazio.WorkersSemTelemetria.Should().BeTrue();
        vazio.IntegracoesSemTelemetria.Should().BeTrue();

        var comWorker = new PainelOperacoesVm(
            [new RegistroWorkerVm("outbox", true, false, "Ativo", "ok", null, null, null, 0, null, null, 0, 0)],
            [], 0, 0, DateTimeOffset.UtcNow);
        comWorker.WorkersSemTelemetria.Should().BeFalse();
    }

    // ---- Promoção do menu: Auditoria/Operações deixam de ser "em preparação" (§14) ----

    [Theory]
    [InlineData("/auditoria/operacoes", Permissoes.AuditoriaVisualizar)]
    [InlineData("/auditoria/incidentes", Permissoes.SegurancaIncidentesGerir)]
    [InlineData("/operacoes/saude", Permissoes.SegurancaIncidentesGerir)]
    [InlineData("/operacoes/workers", Permissoes.SegurancaIncidentesGerir)]
    [InlineData("/operacoes/integracoes", Permissoes.SegurancaIncidentesGerir)]
    public void Rotas_De_Admin_Operacoes_Nao_Estao_Em_Preparacao(string rota, string permissao)
    {
        var item = NavegacaoPortal.EncontrarPorRota(rota);
        item.Should().NotBeNull();
        item!.EmPreparacao.Should().BeFalse();
        item.PermissaoNecessaria.Should().Be(permissao);
    }

    // ---- Prompt 72: /admin/usuarios ganhou read-model REAL (listagem por tenant) e SAIU da preparação.
    // Não resta nenhuma rota administrativa/operacional "em preparação" (Placeholder removido).

    [Fact]
    public void Admin_Usuarios_Agora_E_Real_Nao_Esta_Em_Preparacao()
    {
        var item = NavegacaoPortal.EncontrarPorRota("/admin/usuarios");
        item.Should().NotBeNull();
        item!.EmPreparacao.Should().BeFalse();
        item.PermissaoNecessaria.Should().Be(Permissoes.AdminUsuarios);
    }

    // ---- Prompt 73: STEP-UP de MFA in-context (P2-16 / F70-04) — só caminho RELATIVO no returnUrl ----

    [Fact]
    public void StepUpMfa_Preserva_Caminho_E_Query_Como_ReturnUrl_Relativo()
    {
        var alvo = MapeadorAcaoAdmin.CaminhoStepUpMfa("https://portal.exemplo/dados/processo/abc?x=1");
        alvo.Should().Be("/conta/verificar?returnUrl=" + Uri.EscapeDataString("/dados/processo/abc?x=1"));
    }

    [Fact]
    public void StepUpMfa_Nunca_Retorna_Host_Externo_No_ReturnUrl()
    {
        // Mesmo recebendo uma URL absoluta, o returnUrl carrega apenas PathAndQuery (open-redirect é barrado).
        var alvo = MapeadorAcaoAdmin.CaminhoStepUpMfa("https://atacante.example/evil");
        alvo.Should().Be("/conta/verificar?returnUrl=" + Uri.EscapeDataString("/evil"));
        alvo.Should().NotContain("atacante.example");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StepUpMfa_Uri_Vazia_Falha(string uri)
    {
        var acao = () => MapeadorAcaoAdmin.CaminhoStepUpMfa(uri);
        acao.Should().Throw<ArgumentException>();
    }

    // ---- Backups (Operações → Backups) ----

    [Fact]
    public void Backup_Vm_Mostra_Alvo_Situacao_E_Tamanho_Sem_Detalhe_Fisico()
    {
        var tenant = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        var agora = DateTimeOffset.UnixEpoch;
        var catalogo = DadosBackups.Mapear(new PortalBase.Application.Backup.ItemBackup(Guid.NewGuid(), PortalBase.Domain.Backup.TipoBackup.CatalogoCompleto,
            null, null, PortalBase.Domain.Backup.StatusBackup.Concluido, agora, agora, 5 * 1024 * 1024, true, agora, false, null));
        catalogo.Alvo.Should().Be("Catálogo da plataforma");
        catalogo.SituacaoBadge.Should().Be("ok");
        catalogo.Tamanho.Should().Be("5,0 MB");

        var semNome = DadosBackups.Mapear(new PortalBase.Application.Backup.ItemBackup(Guid.NewGuid(), PortalBase.Domain.Backup.TipoBackup.TenantCompleto,
            tenant, null, PortalBase.Domain.Backup.StatusBackup.Falhou, agora, null, 0, false, null, false, "IOException"));
        semNome.Alvo.Should().Be("Contribuinte 0f1e2d3c");
        semNome.SituacaoRotulo.Should().Be("Falhou");
        semNome.SituacaoBadge.Should().Be("bad");
        semNome.Tamanho.Should().Be("—");
        semNome.Erro.Should().Be("IOException");
    }

    [Theory] // Ações da linha: só backup CONCLUÍDO é testável; só backup de CONTRIBUINTE é restaurável pela tela.
    [InlineData(PortalBase.Domain.Backup.TipoBackup.CatalogoCompleto, PortalBase.Domain.Backup.StatusBackup.Concluido, true, false)]
    [InlineData(PortalBase.Domain.Backup.TipoBackup.TenantCompleto, PortalBase.Domain.Backup.StatusBackup.Concluido, true, true)]
    [InlineData(PortalBase.Domain.Backup.TipoBackup.TenantCompleto, PortalBase.Domain.Backup.StatusBackup.Falhou, false, false)]
    [InlineData(PortalBase.Domain.Backup.TipoBackup.TenantCompleto, PortalBase.Domain.Backup.StatusBackup.Corrompido, false, false)]
    [InlineData(PortalBase.Domain.Backup.TipoBackup.TenantCompleto, PortalBase.Domain.Backup.StatusBackup.Expirado, false, false)]
    [InlineData(PortalBase.Domain.Backup.TipoBackup.TenantCompleto, PortalBase.Domain.Backup.StatusBackup.Iniciado, false, false)]
    public void Backup_Vm_Acoes_Por_Tipo_E_Situacao(
        PortalBase.Domain.Backup.TipoBackup tipo, PortalBase.Domain.Backup.StatusBackup status, bool podeTestar, bool podeRestaurar)
    {
        var tenant = tipo == PortalBase.Domain.Backup.TipoBackup.TenantCompleto ? Guid.NewGuid() : (Guid?)null;
        var vm = DadosBackups.Mapear(new PortalBase.Application.Backup.ItemBackup(Guid.NewGuid(), tipo, tenant, "STAKE", status,
            DateTimeOffset.UnixEpoch, null, 10, true, null, false, null));

        vm.PodeTestar.Should().Be(podeTestar);
        vm.PodeRestaurar.Should().Be(podeRestaurar);
        vm.TenantId.Should().Be(tenant);
    }

    public static TheoryData<Exception, EstadoAcaoUi> ExcecoesBackup() => new()
    {
        { new PortalBase.Application.Excecoes.NaoAutenticadoException(), EstadoAcaoUi.SemPermissao },
        { new PortalBase.Application.Excecoes.AutorizacaoNegadaException(), EstadoAcaoUi.SemPermissao },
        { new PortalBase.Application.Excecoes.MfaRequeridoException(), EstadoAcaoUi.MfaRequerida },
        { new PortalBase.Application.Backup.ExecutorBackupIndisponivelException(), EstadoAcaoUi.Bloqueado },
        { new PortalBase.Application.Backup.BackupBloqueadoPorMigracaoException(), EstadoAcaoUi.Bloqueado },
        { new PortalBase.Application.Backup.OperacaoBackupEmAndamentoException(), EstadoAcaoUi.Conflito },
        { new PortalBase.Domain.Backup.RestauracaoCrossTenantException(), EstadoAcaoUi.Invalido },
        { new PortalBase.Domain.Backup.BackupCorrompidoException(), EstadoAcaoUi.Invalido },
        { new PortalBase.Domain.Backup.BackupInvalidoException("Backup não encontrado no catálogo."), EstadoAcaoUi.Invalido },
        { new PortalBase.Application.Backup.MigracaoAposRestauracaoException(), EstadoAcaoUi.Erro },
    };

    [Theory]
    [MemberData(nameof(ExcecoesBackup))]
    public void Backup_Excecao_Vira_Resultado_Seguro(Exception excecao, EstadoAcaoUi esperado)
    {
        var r = DadosBackups.MapearExcecao(excecao, "A restauração");

        r.Sucesso.Should().BeFalse();
        r.Estado.Should().Be(esperado);
        r.Titulo.Should().NotBeNullOrWhiteSpace();
    }

    [Fact] // Erro técnico inesperado: mensagem genérica — nunca a mensagem crua (pode trazer base, servidor ou senha).
    public void Backup_Excecao_Inesperada_Nao_Vaza_Detalhe_Tecnico()
    {
        var bruta = new InvalidOperationException("Access denied for user 'root'@'db.interno' (using password: YES) on portalbase_tenant_stake_ant20261007120000");

        var r = DadosBackups.MapearExcecao(bruta, "A restauração");

        r.Estado.Should().Be(EstadoAcaoUi.Erro);
        r.Mensagem.Should().NotContain("root").And.NotContain("password").And.NotContain("portalbase_tenant").And.NotContain("db.interno");
    }

    [Fact]
    public void Menu_De_Backups_Exige_A_Permissao_De_Plataforma()
    {
        var item = NavegacaoPortal.Grupos.SelectMany(g => g.Itens).Single(i => i.Rota == "/operacoes/backups");
        item.PermissaoNecessaria.Should().Be(Permissoes.BackupExecutar);
    }
}
