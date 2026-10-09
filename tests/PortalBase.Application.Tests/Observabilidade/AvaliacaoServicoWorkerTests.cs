using FluentAssertions;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Application.Tests.Observabilidade;

/// <summary>
/// OBS-003 (auditoria de 08/10/2026) — a regra única que diz se o SERVIÇO do Worker está no ar, pela batida que ele grava
/// no catálogo. Antes, o health da Web lia a memória da própria Web e o Worker parado não aparecia em lugar nenhum.
/// </summary>
public sealed class AvaliacaoServicoWorkerTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly OpcoesObservabilidade Opcoes = new() { LimiteEstagnacaoSegundos = 300 };

    private static EstadoSaudeWorker Batida(bool ativo, DateTimeOffset ultima)
        => new(AvaliacaoServicoWorker.WorkerBatida, ativo, ultima.AddHours(-1), ultima, ultima, 10, null, null, 0, 0);

    private static EstadoSaudeWorker Trabalhador(string nome, DateTimeOffset ultima, DateTimeOffset? sucesso = null, DateTimeOffset? erro = null)
        => new(nome, true, ultima.AddHours(-1), ultima, sucesso, 1, erro, erro is null ? null : "BackupFalhou", erro is null ? 0 : 1, 0);

    [Fact]
    public void Sem_Nenhuma_Batida_O_Worker_Nunca_Rodou()
    {
        var s = AvaliacaoServicoWorker.Avaliar([], Agora, Opcoes);

        s.Estado.Should().Be(EstadoServicoWorker.NuncaRegistrado);
        s.ServicoForaDoAr.Should().BeTrue();
        s.Mensagem.Should().Contain("nunca registrou");
    }

    [Fact]
    public void Servico_Encerrado_Fica_Parado_Mesmo_Com_A_Ultima_Batida_Recente()
    {
        var s = AvaliacaoServicoWorker.Avaliar([Batida(ativo: false, Agora.AddMinutes(-2))], Agora, Opcoes);

        s.Estado.Should().Be(EstadoServicoWorker.Parado, "parar o serviço do Windows grava Ativo=false: antes isso aparecia como 'Inativo', neutro");
        s.ServicoForaDoAr.Should().BeTrue();
        s.Mensagem.Should().Contain("parado").And.Contain("2 min");
    }

    [Fact]
    public void Batida_Velha_Com_O_Servico_Ainda_Ativo_E_Processo_Que_Caiu()
    {
        var s = AvaliacaoServicoWorker.Avaliar([Batida(ativo: true, Agora.AddMinutes(-20))], Agora, Opcoes);

        s.Estado.Should().Be(EstadoServicoWorker.SemSinal);
        s.Mensagem.Should().Contain("20 min");
    }

    [Fact]
    public void Batida_Recente_Sem_Problema_Esta_Em_Execucao()
    {
        var s = AvaliacaoServicoWorker.Avaliar(
            [Batida(true, Agora.AddSeconds(-20)), Trabalhador("backup-agendado", Agora.AddSeconds(-30), sucesso: Agora.AddHours(-3))], Agora, Opcoes);

        s.Estado.Should().Be(EstadoServicoWorker.EmExecucao);
        s.ServicoForaDoAr.Should().BeFalse();
    }

    [Fact]
    public void Trabalhador_Travado_Ou_Com_A_Ultima_Execucao_Em_Falha_Degrada()
    {
        var s = AvaliacaoServicoWorker.Avaliar(
        [
            Batida(true, Agora.AddSeconds(-20)),
            Trabalhador("outbox", Agora.AddMinutes(-30)),
            Trabalhador("backup-agendado", Agora.AddSeconds(-10), sucesso: Agora.AddHours(-30), erro: Agora.AddHours(-6)),
            Trabalhador("retencao", Agora.AddSeconds(-10), sucesso: Agora.AddHours(-1), erro: Agora.AddHours(-2)),
        ], Agora, Opcoes);

        s.Estado.Should().Be(EstadoServicoWorker.Degradado);
        s.Travados.Should().Equal("outbox");
        s.ComFalha.Should().Equal(["backup-agendado"], "a retenção falhou mas teve sucesso depois: não está em falha");
        s.ServicoForaDoAr.Should().BeFalse();
    }

    [Fact]
    public void Servidor_Sem_Worker_Por_Configuracao_Nao_E_Problema()
    {
        var s = AvaliacaoServicoWorker.Avaliar([], Agora, new OpcoesObservabilidade { WorkerEsperado = false });

        s.Estado.Should().Be(EstadoServicoWorker.NaoEsperado);
        s.ServicoForaDoAr.Should().BeFalse();
    }
}
