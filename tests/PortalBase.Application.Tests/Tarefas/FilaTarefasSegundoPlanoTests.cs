using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Tarefas;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Tarefas;

namespace PortalBase.Application.Tests.Tarefas;

/// <summary>
/// ARQ-006 (auditoria de 08/10/2026): a TELA só enfileira e acompanha. Fail-closed (contribuinte autorizado e permissão
/// no servidor ANTES de gravar o pedido); contribuinte e solicitante vêm do servidor; pedido repetido com o primeiro
/// ativo reaproveita o primeiro; só se acompanha tarefa do contribuinte atual; a espera termina com o aviso do executor.
/// </summary>
public sealed class FilaTarefasSegundoPlanoTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly ContextoTenantFake _tenant = new() { TenantId = Guid.NewGuid() };
    private readonly ContextoSegurancaFake _seguranca = new() { PrincipalId = Guid.NewGuid().ToString(), EstaAutenticado = true };
    private readonly ServicoAutorizacaoFake _autorizacao = new() { Permitir = true };
    private readonly RepositorioTarefasFake _tarefas = new();
    private readonly SinalTarefas _sinal = new();

    public void Dispose() => _sinal.Dispose();

    private FilaTarefasSegundoPlano Fila(IRelogio? relogio = null)
        => new(_tenant, _seguranca, _autorizacao, _tarefas, _sinal, relogio ?? new RelogioFixo(T0));

    private Task<SituacaoTarefa> EnfileirarAsync(string chave = "arquivo:1:2026-09")
        => Fila().EnfileirarAsync(TipoTarefaSegundoPlano.Exemplo, "{\"arquivoId\":\"1\"}", chave, Permissoes.DadosImportar);

    [Fact]
    public async Task Enfileirar_Grava_Com_Contribuinte_E_Solicitante_Do_Servidor_E_Acorda_O_Executor()
    {
        var situacao = await EnfileirarAsync();

        var gravada = _tarefas.Itens.Should().ContainSingle().Subject;
        gravada.Id.Should().Be(situacao.Id);
        gravada.TenantId.Should().Be(_tenant.TenantId!.Value);
        gravada.SolicitanteId.Should().Be(Guid.Parse(_seguranca.PrincipalId!));
        gravada.Estado.Should().Be(EstadoTarefaSegundoPlano.Pendente);
        situacao.Ativa.Should().BeTrue();
        _autorizacao.Chamadas.Should().Be(1);
        await _sinal.Invoking(s => s.AguardarNovaAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
            .Should().CompleteWithinAsync(TimeSpan.FromSeconds(2), "o executor deste processo é acordado na hora");
    }

    [Fact]
    public async Task Sem_Contribuinte_Autorizado_Nada_E_Gravado()
    {
        _tenant.Estado = EstadoTenant.Invalido;

        await Fila().Invoking(f => f.EnfileirarAsync(TipoTarefaSegundoPlano.Exemplo, "{}", "sapb1:2026-09", Permissoes.DadosImportar))
            .Should().ThrowAsync<TenantContextAusenteException>();

        _tarefas.Itens.Should().BeEmpty();
    }

    [Fact]
    public async Task Sem_Permissao_Nada_E_Gravado()
    {
        _autorizacao.Permitir = false;

        await Fila().Invoking(f => f.EnfileirarAsync(TipoTarefaSegundoPlano.Exemplo, "{}", "sapb1:2026-09", Permissoes.DadosImportar))
            .Should().ThrowAsync<AutorizacaoNegadaException>();

        _tarefas.Itens.Should().BeEmpty();
    }

    [Fact]
    public async Task Sem_Usuario_Autenticado_Nada_E_Gravado()
    {
        _seguranca.PrincipalId = null;

        await Fila().Invoking(f => f.EnfileirarAsync(TipoTarefaSegundoPlano.Exemplo, "{}", "sapb1:2026-09", Permissoes.DadosImportar))
            .Should().ThrowAsync<NaoAutenticadoException>();

        _tarefas.Itens.Should().BeEmpty();
    }

    [Fact]
    public async Task Pedido_Repetido_Com_O_Primeiro_Ativo_Reaproveita_E_Depois_De_Terminar_Cria_Outro()
    {
        var primeiro = await EnfileirarAsync();
        var repetido = await EnfileirarAsync();
        var outroArquivo = await EnfileirarAsync("arquivo:2:2026-09");

        repetido.Id.Should().Be(primeiro.Id, "clique duplo ou duas abas não processam o mesmo arquivo duas vezes");
        outroArquivo.Id.Should().NotBe(primeiro.Id);

        var tarefa = _tarefas.Itens.Single(t => t.Id == primeiro.Id);
        tarefa.Reivindicar(T0, TimeSpan.FromMinutes(4), "m");
        tarefa.Concluir("{}", T0);
        (await EnfileirarAsync()).Id.Should().NotBe(primeiro.Id, "terminado, um novo pedido é uma nova execução");
    }

    [Fact]
    public async Task Acompanhar_Tarefa_De_Outro_Contribuinte_Devolve_Nada()
    {
        var situacao = await EnfileirarAsync();
        _tenant.TenantId = Guid.NewGuid();

        (await Fila().ObterAsync(situacao.Id)).Should().BeNull();
        (await Fila().AguardarAsync(situacao.Id, TimeSpan.FromSeconds(1))).Should().BeNull();
    }

    [Fact]
    public async Task Aguardar_Termina_Com_O_Aviso_Do_Executor()
    {
        var situacao = await EnfileirarAsync();
        var relogio = new RelogioReal();
        var espera = Fila(relogio).AguardarAsync(situacao.Id, TimeSpan.FromSeconds(30));

        var tarefa = _tarefas.Itens.Single();
        tarefa.Reivindicar(relogio.AgoraUtc, TimeSpan.FromMinutes(4), "m");
        tarefa.Concluir("{\"ok\":true}", relogio.AgoraUtc);
        await Task.Delay(100);
        _sinal.Concluida(tarefa.Id);

        var final = await espera.WaitAsync(TimeSpan.FromSeconds(10));
        final!.Estado.Should().Be(EstadoTarefaSegundoPlano.Concluida);
        final.Resultado.Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task Aguardar_Devolve_Em_Andamento_Quando_O_Tempo_Acaba()
    {
        var situacao = await EnfileirarAsync();

        var final = await Fila(new RelogioReal()).AguardarAsync(situacao.Id, TimeSpan.FromMilliseconds(300));

        final!.Ativa.Should().BeTrue();
        final.Estado.Should().Be(EstadoTarefaSegundoPlano.Pendente);
    }

    [Theory]
    [InlineData(0, "não tem mais acesso")]
    [InlineData(1, "não tem mais permissão")]
    [InlineData(2, "Competência inválida.")]
    [InlineData(3, "Falha inesperada")]
    public void Motivo_Da_Falha_E_Seguro_Para_A_Tela(int caso, string esperado)
    {
        Exception ex = caso switch
        {
            0 => new TenantNaoAutorizadoException(),
            1 => new AutorizacaoNegadaException(),
            2 => new EntradaInvalidaException("Competência inválida."),
            _ => new InvalidOperationException("Server=db;Password=segredo"),
        };

        var motivo = ExecutorTarefasSegundoPlano.MotivoSeguro(ex);

        motivo.Should().Contain(esperado);
        motivo.Should().NotContain("segredo").And.NotContain("Server=");
    }

    private sealed class RelogioReal : IRelogio
    {
        public DateTimeOffset AgoraUtc => DateTimeOffset.UtcNow;
    }

    private sealed class RepositorioTarefasFake : IRepositorioTarefas
    {
        public List<TarefaSegundoPlano> Itens { get; } = [];

        public Task AdicionarAsync(TarefaSegundoPlano tarefa, CancellationToken cancellationToken = default)
        {
            Itens.Add(tarefa);
            return Task.CompletedTask;
        }

        public Task<TarefaSegundoPlano?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Itens.Find(t => t.Id == id));

        public Task<TarefaSegundoPlano?> LerAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Itens.Find(t => t.Id == id));

        public Task<TarefaSegundoPlano?> ObterAtivaAsync(Guid tenantId, TipoTarefaSegundoPlano tipo, string chaveIdempotencia, CancellationToken cancellationToken = default)
            => Task.FromResult(Itens.LastOrDefault(t => t.TenantId == tenantId && t.Tipo == tipo && t.ChaveIdempotencia == chaveIdempotencia && t.Ativa));

        public Task<IReadOnlyList<TarefaSegundoPlano>> ListarReivindicaveisAsync(DateTimeOffset agoraUtc, int limite, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TarefaSegundoPlano>>(Itens.Where(t => t.PodeSerReivindicada(agoraUtc)).Take(limite).ToList());

        public Task SalvarAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TentarSalvarAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
