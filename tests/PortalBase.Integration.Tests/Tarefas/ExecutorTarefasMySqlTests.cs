using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.DependencyInjection;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Tarefas;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Tarefas;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Tarefas;

/// <summary>
/// ARQ-006 (auditoria de 08/10/2026), contra o catálogo MySQL REAL: o executor das tarefas em segundo plano roda com a
/// identidade de QUEM PEDIU, no contribuinte resolvido no servidor (acesso revogado ⇒ falha sem executar); dois
/// executores nunca rodam a mesma tarefa; a execução interrompida é retomada depois do prazo, sabendo que foi
/// interrompida; o motivo da falha é seguro para a tela. Pulado sem MySQL de teste.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class ExecutorTarefasMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public ExecutorTarefasMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    private sealed record Cenario(ServiceProvider Provedor, Roteiro Roteiro, Relogio Relogio, Guid Tenant, Guid Usuario) : IAsyncDisposable
    {
        public ExecutorTarefasSegundoPlano Executor => Provedor.GetRequiredService<ExecutorTarefasSegundoPlano>();

        public ValueTask DisposeAsync() => Provedor.DisposeAsync();
    }

    private async Task<Cenario> PrepararAsync(bool vincular = true)
    {
        var tenant = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        if (vincular)
        {
            await CenarioMultiTenant.VincularAsync(_fixture, usuario, tenant);
        }

        // Mesma composição de produção (catálogo dedicado da fixture) + o que o HOST do executor registra.
        var configuracao = _fixture.Servicos.GetRequiredService<IConfiguration>();
        var roteiro = new Roteiro();
        var relogio = new Relogio();
        var servicos = new ServiceCollection();
        servicos.AddSingleton(configuracao);
        servicos.AddLogging();
        servicos.AddApplication();
        servicos.AddInfrastructure(configuracao, conexaoObrigatoria: true);
        servicos.AddSingleton<IRelogio>(relogio);
        servicos.AddSingleton(roteiro);
        servicos.AddSingleton<IDefinidorIdentidadeTarefa, DefinidorTeste>();
        servicos.AddScoped<IManipuladorTarefa, ManipuladorRoteiro>();
        servicos.AddSingleton(sp => new ExecutorTarefasSegundoPlano(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IDefinidorIdentidadeTarefa>(), sp.GetRequiredService<SinalTarefas>(),
            relogio, new OpcoesTarefas(), NullLogger<ExecutorTarefasSegundoPlano>.Instance));
        var provedor = servicos.BuildServiceProvider();

        // Fila limpa: tarefas ativas de outros testes não entram na frente.
        await using (var escopo = provedor.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().TarefasSegundoPlano
                .Where(t => t.Estado == EstadoTarefaSegundoPlano.Pendente || t.Estado == EstadoTarefaSegundoPlano.EmExecucao)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Estado, EstadoTarefaSegundoPlano.Falhou));
        }

        return new Cenario(provedor, roteiro, relogio, tenant, Guid.Parse(usuario));
    }

    private static async Task<Guid> CriarTarefaAsync(Cenario c, string parametros = "{\"x\":1}")
    {
        var tarefa = TarefaSegundoPlano.Criar(TipoTarefaSegundoPlano.Exemplo, c.Tenant, c.Usuario, parametros, "teste:" + Guid.NewGuid().ToString("N"), c.Relogio.AgoraUtc);
        await using var escopo = c.Provedor.CreateAsyncScope();
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioTarefas>();
        await repositorio.AdicionarAsync(tarefa);
        await repositorio.SalvarAsync();
        return tarefa.Id;
    }

    private static async Task<TarefaSegundoPlano> LerAsync(Cenario c, Guid id)
    {
        await using var escopo = c.Provedor.CreateAsyncScope();
        return (await escopo.ServiceProvider.GetRequiredService<IRepositorioTarefas>().LerAsync(id))!;
    }

    [SkippableFact]
    public async Task Executa_Com_A_Identidade_De_Quem_Pediu_No_Contribuinte_Dele_E_Grava_O_Resultado()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        await using var c = await PrepararAsync();
        c.Roteiro.Acao = (parametros, _, _) => Task.FromResult("{\"recebido\":" + parametros + "}");
        var id = await CriarTarefaAsync(c);

        (await c.Executor.ExecutarProximaAsync(CancellationToken.None)).Should().BeTrue();

        var execucao = c.Roteiro.Execucoes.Should().ContainSingle().Subject;
        execucao.Principal.Should().Be(c.Usuario.ToString(), "roda com a identidade de quem pediu");
        execucao.Tenant.Should().Be(c.Tenant, "no contribuinte resolvido no servidor");
        execucao.TenantAutorizado.Should().BeTrue();
        execucao.Retomada.Should().BeFalse();
        var tarefa = await LerAsync(c, id);
        tarefa.Estado.Should().Be(EstadoTarefaSegundoPlano.Concluida);
        tarefa.Resultado.Should().Be("{\"recebido\":{\"x\":1}}");
        tarefa.Tentativas.Should().Be(1);
        (await c.Executor.ExecutarProximaAsync(CancellationToken.None)).Should().BeFalse("nada mais na fila");
    }

    [SkippableFact]
    public async Task Sem_Acesso_Ao_Contribuinte_A_Tarefa_Falha_Sem_Executar()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        await using var c = await PrepararAsync(vincular: false); // acesso revogado depois do pedido
        var id = await CriarTarefaAsync(c);

        (await c.Executor.ExecutarProximaAsync(CancellationToken.None)).Should().BeTrue();

        c.Roteiro.Execucoes.Should().BeEmpty("o manipulador nunca roda sem o contribuinte autorizado");
        var tarefa = await LerAsync(c, id);
        tarefa.Estado.Should().Be(EstadoTarefaSegundoPlano.Falhou);
        tarefa.Motivo.Should().Contain("não tem mais acesso");
    }

    [SkippableFact]
    public async Task Erro_De_Regra_Vira_Motivo_E_Erro_Inesperado_Nao_Expoe_Detalhe()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        await using var c = await PrepararAsync();
        c.Roteiro.Acao = (parametros, _, _) => parametros.Contains("regra", StringComparison.Ordinal)
            ? throw new EntradaInvalidaException("Competência inválida.")
            : throw new InvalidOperationException("Server=db-interno;Password=segredo");
        var regra = await CriarTarefaAsync(c, "{\"caso\":\"regra\"}");
        var inesperado = await CriarTarefaAsync(c, "{\"caso\":\"outro\"}");

        await c.Executor.ExecutarProximaAsync(CancellationToken.None);
        await c.Executor.ExecutarProximaAsync(CancellationToken.None);

        (await LerAsync(c, regra)).Motivo.Should().Be("Competência inválida.");
        var falha = await LerAsync(c, inesperado);
        falha.Estado.Should().Be(EstadoTarefaSegundoPlano.Falhou);
        falha.Motivo.Should().Contain("Falha inesperada").And.NotContain("segredo").And.NotContain("Server=");
    }

    [SkippableFact]
    public async Task Dois_Executores_Nunca_Rodam_A_Mesma_Tarefa()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        await using var c = await PrepararAsync();
        c.Roteiro.Acao = async (_, _, ct) =>
        {
            await Task.Delay(300, ct);
            return "{}";
        };
        var id = await CriarTarefaAsync(c);
        var outro = new ExecutorTarefasSegundoPlano(
            c.Provedor.GetRequiredService<IServiceScopeFactory>(), c.Provedor.GetRequiredService<IDefinidorIdentidadeTarefa>(),
            c.Provedor.GetRequiredService<SinalTarefas>(), c.Relogio, new OpcoesTarefas(), NullLogger<ExecutorTarefasSegundoPlano>.Instance);

        var resultados = await Task.WhenAll(
            c.Executor.ExecutarProximaAsync(CancellationToken.None), outro.ExecutarProximaAsync(CancellationToken.None),
            c.Executor.ExecutarProximaAsync(CancellationToken.None), outro.ExecutarProximaAsync(CancellationToken.None));

        resultados.Count(r => r).Should().Be(1, "só um executor reivindica a tarefa (concorrência otimista)");
        c.Roteiro.Execucoes.Should().ContainSingle();
        (await LerAsync(c, id)).Estado.Should().Be(EstadoTarefaSegundoPlano.Concluida);
    }

    [SkippableFact]
    public async Task Execucao_Interrompida_E_Retomada_Depois_Do_Prazo_Sabendo_Que_Foi_Interrompida()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        await using var c = await PrepararAsync();
        var comecou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Roteiro.Acao = async (_, retomada, ct) =>
        {
            if (retomada)
            {
                return "{\"retomada\":true}";
            }

            comecou.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // o processo "cai" no meio
            return "{}";
        };
        var id = await CriarTarefaAsync(c);

        using (var desligamento = new CancellationTokenSource())
        {
            var execucao = c.Executor.ExecutarProximaAsync(desligamento.Token);
            await comecou.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await desligamento.CancelAsync();
            await execucao.Invoking(e => e).Should().ThrowAsync<OperationCanceledException>();
        }

        var interrompida = await LerAsync(c, id);
        interrompida.Estado.Should().Be(EstadoTarefaSegundoPlano.EmExecucao, "fica em execução; o prazo vence e ela volta");
        (await c.Executor.ExecutarProximaAsync(CancellationToken.None)).Should().BeFalse("ninguém pega antes do prazo vencer");

        c.Relogio.AgoraUtc += new OpcoesTarefas().PrazoReivindicacao + TimeSpan.FromSeconds(1);
        (await c.Executor.ExecutarProximaAsync(CancellationToken.None)).Should().BeTrue();

        c.Roteiro.Execucoes.Select(e => e.Retomada).Should().Equal([false, true]);
        var tarefa = await LerAsync(c, id);
        tarefa.Estado.Should().Be(EstadoTarefaSegundoPlano.Concluida);
        tarefa.Tentativas.Should().Be(2);
        tarefa.Resultado.Should().Be("{\"retomada\":true}");
    }

    [SkippableFact]
    public async Task Interrompida_Vezes_Demais_Desiste_Sem_Executar()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        await using var c = await PrepararAsync();
        var id = await CriarTarefaAsync(c);
        await using (var escopo = c.Provedor.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().TarefasSegundoPlano.Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Estado, EstadoTarefaSegundoPlano.EmExecucao)
                    .SetProperty(t => t.Tentativas, TarefaSegundoPlano.MaximoTentativas)
                    .SetProperty(t => t.LeaseAteUtc, c.Relogio.AgoraUtc.AddMinutes(-1)));
        }

        (await c.Executor.ExecutarProximaAsync(CancellationToken.None)).Should().BeFalse();

        c.Roteiro.Execucoes.Should().BeEmpty();
        var tarefa = await LerAsync(c, id);
        tarefa.Estado.Should().Be(EstadoTarefaSegundoPlano.Falhou);
        tarefa.Motivo.Should().Contain("interrompida");
    }

    // ---- Dobras do HOST (o resto é a composição de produção) ----

    private sealed class Relogio : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed record Execucao(string? Principal, Guid? Tenant, bool TenantAutorizado, bool Retomada);

    private sealed class Roteiro
    {
        public ConcurrentQueue<Execucao> Fila { get; } = new();

        public IReadOnlyList<Execucao> Execucoes => [.. Fila];

        public Func<string, bool, CancellationToken, Task<string>> Acao { get; set; } = (_, _, _) => Task.FromResult("{}");
    }

    /// <summary>Como o host de jobs: a identidade de quem pediu no contexto de segurança do escopo.</summary>
    private sealed class DefinidorTeste : IDefinidorIdentidadeTarefa
    {
        public void Definir(IServiceProvider escopo, Guid solicitanteId)
            => escopo.GetRequiredService<IContextoSegurancaMutavel>().Definir(solicitanteId.ToString(), mfaSatisfeito: false);
    }

    private sealed class ManipuladorRoteiro(Roteiro roteiro, IContextoSeguranca seguranca, ITenantContext tenant) : IManipuladorTarefa
    {
        public TipoTarefaSegundoPlano Tipo => TipoTarefaSegundoPlano.Exemplo;

        public Task<string> ExecutarAsync(string parametros, bool retomada, CancellationToken cancellationToken)
        {
            roteiro.Fila.Enqueue(new Execucao(seguranca.PrincipalId, tenant.TenantId, tenant.EstaAutorizado, retomada));
            return roteiro.Acao(parametros, retomada, cancellationToken);
        }
    }
}
