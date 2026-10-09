using FluentAssertions;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Tarefas;

namespace PortalBase.Domain.Tests.Tarefas;

/// <summary>
/// ARQ-006 (auditoria de 08/10/2026): a tarefa em segundo plano — ciclo de vida, prazo de reivindicação (uma execução
/// por vez; vencido, outra retoma), limite de execuções interrompidas e limites dos textos gravados.
/// </summary>
public sealed class TarefaSegundoPlanoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Prazo = TimeSpan.FromMinutes(4);

    private static TarefaSegundoPlano Nova()
        => TarefaSegundoPlano.Criar(TipoTarefaSegundoPlano.Exemplo, Guid.NewGuid(), Guid.NewGuid(), "{\"a\":1}", "arquivo:x:2026-09", T0);

    [Fact]
    public void Criada_Pendente_E_Reivindicavel()
    {
        var t = Nova();

        t.Estado.Should().Be(EstadoTarefaSegundoPlano.Pendente);
        t.Ativa.Should().BeTrue();
        t.Tentativas.Should().Be(0);
        t.PodeSerReivindicada(T0).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Criar_Recusa_Entrada_Invalida(int caso)
    {
        var acao = caso switch
        {
            0 => (Action)(() => TarefaSegundoPlano.Criar((TipoTarefaSegundoPlano)99, Guid.NewGuid(), Guid.NewGuid(), "{}", "k", T0)),
            1 => () => TarefaSegundoPlano.Criar(TipoTarefaSegundoPlano.Exemplo, Guid.Empty, Guid.NewGuid(), "{}", "k", T0),
            2 => () => TarefaSegundoPlano.Criar(TipoTarefaSegundoPlano.Exemplo, Guid.NewGuid(), Guid.NewGuid(), new string('x', TarefaSegundoPlano.TamanhoMaximoParametros + 1), "k", T0),
            _ => () => TarefaSegundoPlano.Criar(TipoTarefaSegundoPlano.Exemplo, Guid.NewGuid(), Guid.NewGuid(), "{}", " ", T0),
        };

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Reivindicada_Fica_Com_Uma_Execucao_Ate_O_Prazo_Vencer()
    {
        var t = Nova();

        t.Reivindicar(T0, Prazo, "maquina:1").Should().BeTrue();

        t.Estado.Should().Be(EstadoTarefaSegundoPlano.EmExecucao);
        t.Tentativas.Should().Be(1);
        t.IniciadaEmUtc.Should().Be(T0);
        t.LeaseAteUtc.Should().Be(T0 + Prazo);
        t.PodeSerReivindicada(T0 + Prazo - TimeSpan.FromSeconds(1)).Should().BeFalse("outro executor não pega enquanto o prazo vale");
        ((Action)(() => t.Reivindicar(T0.AddMinutes(1), Prazo, "maquina:2"))).Should().Throw<EstadoInvalidoException>();
        t.PodeSerReivindicada(T0 + Prazo).Should().BeTrue("o processo que a tinha caiu: retoma");
    }

    [Fact]
    public void Renovar_Estende_O_Prazo_E_Muda_O_Selo_De_Concorrencia()
    {
        var t = Nova();
        t.Reivindicar(T0, Prazo, "m");
        var selo = t.ConcurrencyStamp;

        t.Renovar(T0.AddMinutes(2), Prazo);

        t.LeaseAteUtc.Should().Be(T0.AddMinutes(2) + Prazo);
        t.ConcurrencyStamp.Should().NotBe(selo);
    }

    [Fact]
    public void Depois_Do_Limite_De_Execucoes_Interrompidas_Desiste_Com_Motivo()
    {
        var t = Nova();
        var agora = T0;
        for (var i = 1; i <= TarefaSegundoPlano.MaximoTentativas; i++)
        {
            t.Reivindicar(agora, Prazo, "m").Should().BeTrue();
            agora += Prazo; // caiu de novo: o prazo vence
        }

        t.Reivindicar(agora, Prazo, "m").Should().BeFalse();

        t.Estado.Should().Be(EstadoTarefaSegundoPlano.Falhou);
        t.Motivo.Should().Contain("interrompida");
        t.Ativa.Should().BeFalse();
    }

    [Fact]
    public void Concluir_Grava_Resultado_E_Encerra()
    {
        var t = Nova();
        t.Reivindicar(T0, Prazo, "m");

        t.Concluir("{\"ok\":true}", T0.AddSeconds(5));

        t.Estado.Should().Be(EstadoTarefaSegundoPlano.Concluida);
        t.Resultado.Should().Be("{\"ok\":true}");
        t.ConcluidaEmUtc.Should().Be(T0.AddSeconds(5));
        t.LeaseAteUtc.Should().BeNull();
        ((Action)(() => t.Falhar("x", T0))).Should().Throw<EstadoInvalidoException>("terminada não muda mais");
        ((Action)(() => t.Concluir(null, T0))).Should().Throw<EstadoInvalidoException>();
    }

    [Fact]
    public void Concluir_Exige_Execucao()
        => ((Action)(() => Nova().Concluir("{}", T0))).Should().Throw<EstadoInvalidoException>();

    [Fact]
    public void Falhar_Limita_O_Motivo_E_Usa_Um_Padrao_Quando_Vazio()
    {
        var longa = Nova();
        longa.Reivindicar(T0, Prazo, "m");
        longa.Falhar(new string('m', 2_000), T0);
        longa.Motivo.Should().HaveLength(TarefaSegundoPlano.TamanhoMaximoMotivo);

        var vazia = Nova();
        vazia.Falhar("  ", T0);
        vazia.Motivo.Should().NotBeNullOrWhiteSpace();
        vazia.Estado.Should().Be(EstadoTarefaSegundoPlano.Falhou);
    }
}
