using System.Text.Json;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Tarefas;

namespace PortalBase.Application.Tarefas;

/// <summary>JSON dos parâmetros e resultados das tarefas (sempre o mesmo formato, sem nada sensível).</summary>
public static class JsonTarefas
{
    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web);

    public static string Serializar<T>(T valor) => JsonSerializer.Serialize(valor, Opcoes);

    public static T Ler<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Opcoes) ?? throw new EntradaInvalidaException("Parâmetros da tarefa inválidos.");
}

/// <summary>Parâmetros (técnicos) da tarefa de exemplo — nunca segredo, conteúdo de arquivo ou dado pessoal.</summary>
public sealed record ParametrosTarefaExemplo(string Texto)
{
    /// <summary>Chave de idempotência: o mesmo pedido ativo não é enfileirado duas vezes.</summary>
    public string Chave() => "exemplo:" + Texto;
}

/// <summary>Resultado da tarefa de exemplo.</summary>
public sealed record ResultadoTarefaExemplo(int Caracteres, bool Retomada);

/// <summary>
/// Manipulador de EXEMPLO do template: mostra o contrato de uma tarefa em segundo plano (parâmetros em JSON, resultado em
/// JSON, retomada após queda do processo). Copie este arquivo para cada trabalho pesado do seu domínio (importação,
/// geração de relatório, integração): o caso de uso roda no escopo da tarefa, já com o solicitante e o contribuinte
/// resolvidos pelo executor, e continua autorizando no servidor como faria na tela.
/// </summary>
public sealed class ManipuladorTarefaExemplo : IManipuladorTarefa
{
    public TipoTarefaSegundoPlano Tipo => TipoTarefaSegundoPlano.Exemplo;

    public Task<string> ExecutarAsync(string parametros, bool retomada, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = JsonTarefas.Ler<ParametrosTarefaExemplo>(parametros);
        if (string.IsNullOrWhiteSpace(p.Texto))
        {
            throw new EntradaInvalidaException("Informe o texto da tarefa de exemplo.");
        }

        return Task.FromResult(JsonTarefas.Serializar(new ResultadoTarefaExemplo(p.Texto.Length, retomada)));
    }
}
