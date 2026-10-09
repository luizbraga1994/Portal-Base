using FluentAssertions;
using PortalBase.Application.Producao;

namespace PortalBase.Application.Tests.Producao;

/// <summary>
/// Testes do mecanismo objetivo de PRONTIDÃO PARA PRODUÇÃO (Prompt 33): classificação de ambiente,
/// validação fail-fast de configuração (só presença, nunca segredo), avaliação de portões em decisão
/// (READY/READY_WITH_CONDITIONS/NOT_READY) e disponibilidade fail-closed de funcionalidades fiscais.
/// </summary>
public sealed class ProntidaoProducaoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // ───────────── Ambiente ─────────────

    [Theory]
    [InlineData("Development", AmbienteImplantacao.Development, false)]
    [InlineData("Test", AmbienteImplantacao.Test, false)]
    [InlineData("Staging", AmbienteImplantacao.Staging, false)]
    [InlineData("ProductionRestricted", AmbienteImplantacao.ProducaoRestrita, true)]
    [InlineData("Production", AmbienteImplantacao.Producao, true)]
    public void Ambiente_ClassificaEDistingueProdutivo(string nome, AmbienteImplantacao esperado, bool produtivo)
    {
        var ambiente = ClassificadorAmbiente.Classificar(nome);
        ambiente.Should().Be(esperado);
        ClassificadorAmbiente.EhProdutivo(ambiente).Should().Be(produtivo);
    }

    [Fact] // §46: Produção Restrita NUNCA é confundida com Produção.
    public void Ambiente_ProducaoRestrita_DiferenteDeProducao()
        => ClassificadorAmbiente.Classificar("ProductionRestricted").Should().NotBe(AmbienteImplantacao.Producao);

    [Fact] // Nome desconhecido/vazio NÃO é tratado como produtivo (fail-safe).
    public void Ambiente_Desconhecido_NaoEhProdutivo()
    {
        ClassificadorAmbiente.TentarClassificar("qualquer-coisa", out var a).Should().BeFalse();
        ClassificadorAmbiente.EhProdutivo(a).Should().BeFalse();
    }

    // ───────────── Validação de configuração (fail-fast) ─────────────

    [Fact] // §9: produção sem config essencial ⇒ NÃO Ok, com as chaves faltantes.
    public void Config_ProducaoIncompleta_ReportaFaltantes()
    {
        var estado = new EstadoConfiguracaoProducao(
            AmbienteImplantacao.Producao,
            ConexaoCatalogoPresente: false, ModoVault: "ConfiguracaoLocal", DiretorioArmazenamentoPresente: false);

        var r = ValidadorConfiguracaoProducao.Validar(estado);

        r.Ok.Should().BeFalse();
        r.Faltantes.Select(f => f.Chave).Should().Contain([
            "ConnectionStrings:Catalogo", "Vault:Modo", "Armazenamento:DiretorioRaiz"]);
    }

    [Fact] // §9: produção completa ⇒ Ok.
    public void Config_ProducaoCompleta_Ok()
    {
        var estado = new EstadoConfiguracaoProducao(
            AmbienteImplantacao.Producao,
            ConexaoCatalogoPresente: true, ModoVault: "KeyVault", DiretorioArmazenamentoPresente: true);

        ValidadorConfiguracaoProducao.Validar(estado).Ok.Should().BeTrue();
    }

    [Fact] // Servidor próprio (IIS): cofre local do Windows é aceito em produção, desde que a pasta do cofre esteja configurada.
    public void Config_Producao_Com_Cofre_Local_Do_Windows()
    {
        var semPasta = new EstadoConfiguracaoProducao(
            AmbienteImplantacao.Producao,
            ConexaoCatalogoPresente: true, ModoVault: "CofreWindows", DiretorioArmazenamentoPresente: true);

        var r = ValidadorConfiguracaoProducao.Validar(semPasta);
        r.Ok.Should().BeFalse();
        r.Faltantes.Should().ContainSingle(f => f.Chave == "Vault:DiretorioCofreWindows");

        ValidadorConfiguracaoProducao.Validar(semPasta with { DiretorioCofreWindowsPresente = true }).Ok.Should().BeTrue();
        ValidadorConfiguracaoProducao.Validar(semPasta with { ModoVault = "ConfiguracaoLocal", DiretorioCofreWindowsPresente = true })
            .Faltantes.Should().ContainSingle(f => f.Chave == "Vault:Modo");
    }

    [Fact] // Credencial de DDL do catálogo (recriação em Development) é proibida em produção: fail-fast.
    public void Config_Producao_Com_Conta_Migradora_Do_Catalogo_E_Recusada()
    {
        var estado = new EstadoConfiguracaoProducao(
            AmbienteImplantacao.Producao,
            ConexaoCatalogoPresente: true, ModoVault: "KeyVault", DiretorioArmazenamentoPresente: true, ConexaoMigracaoCatalogoPresente: true);

        var r = ValidadorConfiguracaoProducao.Validar(estado);

        r.Ok.Should().BeFalse();
        r.Faltantes.Should().ContainSingle(f => f.Chave == "ConnectionStrings:CatalogoMigracao");
    }

    [Fact] // Em Development a conta migradora é o que permite recriar o catálogo apagado.
    public void Config_Development_Com_Conta_Migradora_E_Aceita()
    {
        var estado = new EstadoConfiguracaoProducao(
            AmbienteImplantacao.Development,
            ConexaoCatalogoPresente: true, ModoVault: "ConfiguracaoLocal", DiretorioArmazenamentoPresente: true, ConexaoMigracaoCatalogoPresente: true);

        ValidadorConfiguracaoProducao.Validar(estado).Ok.Should().BeTrue();
    }

    [Fact] // §10: ambiente NÃO produtivo não impõe exigências fail-fast.
    public void Config_NaoProdutivo_SempreOk()
    {
        var estado = new EstadoConfiguracaoProducao(
            AmbienteImplantacao.Development,
            ConexaoCatalogoPresente: false, ModoVault: "", DiretorioArmazenamentoPresente: false);

        ValidadorConfiguracaoProducao.Validar(estado).Ok.Should().BeTrue();
    }

    // ───────────── Avaliação de portões ─────────────

    private static PortaoProntidao Portao(EstadoPortao estado, bool bloqueador = false)
        => new($"G-{estado}", "area", estado, "evidencia", bloqueador);

    [Fact] // §74: todos Ready/N/A ⇒ READY.
    public void Portoes_TodosReady_Ready()
        => AvaliadorProntidaoProducao.Avaliar([Portao(EstadoPortao.Ready), Portao(EstadoPortao.NaoAplicavel)])
            .Should().Be(DecisaoProntidao.Ready);

    [Fact] // §74: um BLOCKED de infra (não bloqueador) ⇒ READY_WITH_CONDITIONS.
    public void Portoes_ComBlockedNaoBloqueador_ReadyWithConditions()
        => AvaliadorProntidaoProducao.Avaliar([Portao(EstadoPortao.Ready), Portao(EstadoPortao.Blocked)])
            .Should().Be(DecisaoProntidao.ReadyWithConditions);

    [Fact] // §75: portão BLOQUEADOR não satisfeito ⇒ NOT_READY.
    public void Portoes_BloqueadorNaoSatisfeito_NotReady()
        => AvaliadorProntidaoProducao.Avaliar([Portao(EstadoPortao.Ready), Portao(EstadoPortao.Blocked, bloqueador: true)])
            .Should().Be(DecisaoProntidao.NotReady);

    [Fact] // Qualquer NotReady ⇒ NOT_READY.
    public void Portoes_ComNotReady_NotReady()
        => AvaliadorProntidaoProducao.Avaliar([Portao(EstadoPortao.Ready), Portao(EstadoPortao.NotReady)])
            .Should().Be(DecisaoProntidao.NotReady);

    [Fact] // Lista vazia ⇒ NOT_READY (nada avaliado não é "pronto").
    public void Portoes_Vazio_NotReady()
        => AvaliadorProntidaoProducao.Avaliar([]).Should().Be(DecisaoProntidao.NotReady);

    // ───────────── Aprovação ─────────────

    [Fact] // §76: a decisão é derivada dos portões — não é possível "declarar READY" com bloqueador aberto.
    public void Aprovacao_NaoPodeSerReadyComBloqueadorAberto()
    {
        var aprovacao = AprovacaoProntidaoProducao.Criar(
            "2.0.0", "abc1234", AmbienteImplantacao.Producao, Agora, "tech-lead",
            [Portao(EstadoPortao.Ready), Portao(EstadoPortao.NotReady, bloqueador: true)],
            ["risco residual X"]);

        aprovacao.Decisao.Should().Be(DecisaoProntidao.NotReady);
        aprovacao.PermiteProducao.Should().BeFalse();
        aprovacao.PermiteProducaoIrrestrita.Should().BeFalse();
        aprovacao.RiscosResiduais.Should().ContainSingle();
    }

    [Fact] // Aprovação irrestrita só com todos Ready.
    public void Aprovacao_Irrestrita_ComTodosReady()
    {
        var aprovacao = AprovacaoProntidaoProducao.Criar(
            "2.0.0", "abc1234", AmbienteImplantacao.ProducaoRestrita, Agora, "tech-lead",
            [Portao(EstadoPortao.Ready), Portao(EstadoPortao.Ready)]);

        aprovacao.Decisao.Should().Be(DecisaoProntidao.Ready);
        aprovacao.PermiteProducaoIrrestrita.Should().BeTrue();
    }

    [Theory] // Campos obrigatórios validados.
    [InlineData("", "commit", "exec")]
    [InlineData("2.0.0", "", "exec")]
    [InlineData("2.0.0", "commit", "")]
    public void Aprovacao_CamposObrigatorios(string versao, string commit, string executor)
    {
        var acao = () => AprovacaoProntidaoProducao.Criar(
            versao, commit, AmbienteImplantacao.Producao, Agora, executor, [Portao(EstadoPortao.Ready)]);
        acao.Should().Throw<ArgumentException>();
    }

    // ───────────── Disponibilidade de funcionalidade (fail-closed) ─────────────

    [Fact] // §43: sem regra ⇒ INDISPONÍVEL (fail-closed).
    public void Disponibilidade_SemRegra_FailClosed()
        => AvaliadorDisponibilidade.Disponivel([], FuncionalidadeCritica.EnvioArquivos, Agora).Should().BeFalse();

    [Fact] // Habilitada e dentro da janela ⇒ disponível.
    public void Disponibilidade_HabilitadaNaJanela_Disponivel()
    {
        var regras = new[]
        {
            new RegraDisponibilidade(FuncionalidadeCritica.EnvioArquivos, Habilitada: true,
                VigenteDeUtc: Agora.AddDays(-1), VigenteAteUtc: Agora.AddDays(1)),
        };
        AvaliadorDisponibilidade.Disponivel(regras, FuncionalidadeCritica.EnvioArquivos, Agora).Should().BeTrue();
    }

    [Fact] // Antes da vigência ⇒ indisponível.
    public void Disponibilidade_AntesDaVigencia_Indisponivel()
    {
        var regras = new[] { new RegraDisponibilidade(FuncionalidadeCritica.IntegracaoExterna, true, VigenteDeUtc: Agora.AddDays(1)) };
        AvaliadorDisponibilidade.Disponivel(regras, FuncionalidadeCritica.IntegracaoExterna, Agora).Should().BeFalse();
    }

    [Fact] // Desabilitada explicitamente ⇒ indisponível mesmo dentro da janela.
    public void Disponibilidade_Desabilitada_Indisponivel()
    {
        var regras = new[] { new RegraDisponibilidade(FuncionalidadeCritica.IntegracaoExterna, Habilitada: false) };
        AvaliadorDisponibilidade.Disponivel(regras, FuncionalidadeCritica.IntegracaoExterna, Agora).Should().BeFalse();
    }
}
