using System.Diagnostics;
using System.Text;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>Código de saída e saída padrão (limitada a <see cref="ExecutorProcessoExterno.MaximoSaida"/> caracteres).</summary>
public sealed record ResultadoProcessoExterno(int CodigoSaida, string Saida);

/// <summary>
/// Executa um programa externo. Sem shell e sem linha de comando concatenada (cada argumento vai em
/// <see cref="ProcessStartInfo.ArgumentList"/>). A saída é devolvida só para o chamador CLASSIFICAR o resultado: pode
/// citar caminhos de arquivo e nunca vai inteira para log.
/// </summary>
public interface IExecutorProcessoExterno
{
    /// <summary>
    /// Roda até terminar. Estourado o <paramref name="timeout"/>, encerra a árvore de processos e lança
    /// <see cref="TimeoutException"/>; cancelado pelo chamador, encerra e lança <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ResultadoProcessoExterno> ExecutarAsync(
        string executavel, IReadOnlyList<string> argumentos, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Implementação com <see cref="Process"/>.</summary>
public sealed class ExecutorProcessoExterno : IExecutorProcessoExterno
{
    /// <summary>Limite da saída guardada (o resto é drenado e descartado).</summary>
    public const int MaximoSaida = 64 * 1024;

    public async Task<ResultadoProcessoExterno> ExecutarAsync(
        string executavel, IReadOnlyList<string> argumentos, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executavel);
        ArgumentNullException.ThrowIfNull(argumentos);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = executavel,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argumento in argumentos)
        {
            psi.ArgumentList.Add(argumento);
        }

        using var processo = new Process { StartInfo = psi };
        // Drena as duas saídas (evita travar o processo com o buffer cheio); guarda só o começo da saída padrão.
        var saida = new StringBuilder();
        processo.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (saida)
            {
                if (saida.Length < MaximoSaida)
                {
                    saida.Append(e.Data.AsSpan(0, Math.Min(e.Data.Length, MaximoSaida - saida.Length))).Append('\n');
                }
            }
        };
        processo.ErrorDataReceived += static (_, _) => { };

        if (!processo.Start())
        {
            throw new InvalidOperationException("O processo externo não iniciou.");
        }

        processo.BeginOutputReadLine();
        processo.BeginErrorReadLine();

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(timeout);

        try
        {
            await processo.WaitForExitAsync(limite.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Encerrar(processo);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"O processo externo não terminou em {timeout.TotalSeconds:0} s.");
        }

        // Garante que os eventos assíncronos de saída terminaram antes de ler o texto.
        processo.WaitForExit();
        lock (saida)
        {
            return new ResultadoProcessoExterno(processo.ExitCode, saida.ToString());
        }
    }

    private static void Encerrar(Process processo)
    {
        try
        {
            if (!processo.HasExited)
            {
                processo.Kill(entireProcessTree: true);
                processo.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Já terminou entre a verificação e o Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Sem permissão para encerrar um filho já órfão: o processo principal some com o descarte.
        }
    }
}
