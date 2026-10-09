using PortalBase.Application.Tarefas;

namespace PortalBase.Web.Tarefas;

/// <summary>Composição do EXECUTOR de tarefas em segundo plano na Web (ARQ-006).</summary>
public static class ServicosTarefasWeb
{
    /// <summary>
    /// Registra o executor, os manipuladores e a identidade da tarefa. O laço em segundo plano só sobe quando
    /// <paramref name="executorHabilitado"/> (padrão: ligado; nos testes automatizados, só se pedido explicitamente).
    /// </summary>
    public static IServiceCollection AddExecutorTarefas(this IServiceCollection services, bool executorHabilitado)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDefinidorIdentidadeTarefa, DefinidorIdentidadeTarefaWeb>();
        services.AddSingleton<ExecutorTarefasSegundoPlano>();
        // Um manipulador por tipo de tarefa. Troque o exemplo pelos do seu domínio.
        services.AddScoped<IManipuladorTarefa, ManipuladorTarefaExemplo>();
        if (executorHabilitado)
        {
            services.AddHostedService<HospedeiroTarefas>();
        }

        return services;
    }

    /// <summary>
    /// <c>Tarefas:ExecutorHabilitado</c> quando configurado; senão ligado, exceto no ambiente <c>Testing</c> (lá os
    /// testes que precisam do executor o ligam explicitamente).
    /// </summary>
    public static bool ExecutorHabilitado(IConfiguration configuration, IHostEnvironment ambiente)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(ambiente);
        var configurado = configuration.GetValue<bool?>($"{OpcoesTarefas.Secao}:{nameof(OpcoesTarefas.ExecutorHabilitado)}");
        return configurado ?? !ambiente.IsEnvironment("Testing");
    }
}
