using System.Text;
using PortalBase.AcessoLocal;

// Ferramenta de DESENVOLVIMENTO (ver ProgramaAcessoLocal e GuardaAmbienteLocal): recusa tudo que não seja um
// ambiente local. Códigos de saída: 0 sucesso, 1 falha, 2 entrada inválida, 3 recusado pela trava de ambiente.
var terminal = new TerminalConsole();

// Só no console interativo: UTF-8 deixa os acentos legíveis em qualquer página de código (437/850/1252). No Windows
// isto muda a página de código DO CONSOLE (compartilhado com o shell), por isso a original é restaurada ao sair.
// Com a saída redirecionada (pipe/CI), a codificação padrão é mantida: quem lê decodifica com ela.
var codificacaoOriginal = Console.IsOutputRedirected ? null : Console.OutputEncoding;
TrocarCodificacao(codificacaoOriginal is null ? null : Encoding.UTF8);

using var cancelamento = new CancellationTokenSource();
Console.CancelKeyPress += (_, evento) =>
{
    // 1º Ctrl+C: cancela as operações em curso (CancellationToken) para terminar de forma limpa; o 2º encerra já
    // (útil enquanto uma pergunta espera o Enter). O que já foi gravado permanece; rodar de novo completa o resto.
    evento.Cancel = !cancelamento.IsCancellationRequested;
    if (evento.Cancel)
    {
        terminal.Aviso("cancelando... (Ctrl+C de novo encerra imediatamente)");
        cancelamento.Cancel();
    }
};

try
{
    return await ProgramaAcessoLocal.ExecutarAsync(
        args,
        terminal,
        Environment.GetEnvironmentVariable,
        () => ProgramaAcessoLocal.CarregarConfiguracao(AppContext.BaseDirectory),
        Environment.UserName,
        cancelamento.Token);
}
catch (OperationCanceledException)
{
    terminal.Erro("Cancelado. O que já foi concluído permanece (rodar de novo completa o restante).");
    return (int)ResultadoAcessoLocal.Falha;
}
#pragma warning disable CA1031 // Último recurso do executável: falha controlada, sem stack trace (regra 16).
catch (Exception excecao)
#pragma warning restore CA1031
{
    terminal.Erro($"Falha inesperada ({excecao.GetType().Name}): {excecao.Message}");
    terminal.Erro("Rodar de novo é seguro (o comando é idempotente).");
    return (int)ResultadoAcessoLocal.Falha;
}
finally
{
    TrocarCodificacao(codificacaoOriginal);
}

static void TrocarCodificacao(Encoding? codificacao)
{
    if (codificacao is null)
    {
        return;
    }

    try
    {
        Console.OutputEncoding = codificacao;
    }
    catch (IOException)
    {
        // sem console de verdade: mantém a codificação atual
    }
}
