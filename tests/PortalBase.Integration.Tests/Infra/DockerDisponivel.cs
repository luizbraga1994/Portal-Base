using System.Diagnostics;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>Detecta a disponibilidade do Docker para pular testes que dependem dele.</summary>
internal static class DockerDisponivel
{
    public static bool Verificar()
    {
        try
        {
            using var processo = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (processo is null)
            {
                return false;
            }

            return processo.WaitForExit(5000) && processo.ExitCode == 0;
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            return false;
        }
    }
}
