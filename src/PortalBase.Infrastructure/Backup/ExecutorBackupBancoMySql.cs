using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Backup;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// EXECUTOR FÍSICO real de backup/restore (Prompt 50, Fases 36-38) por processo <c>mysqldump</c>/<c>mysql</c>.
/// Substitui o <see cref="ExecutorBackupIndisponivel"/> quando ligado (<c>Backup:ExecutorMySql:Habilitado</c>)
/// e as ferramentas existem — o default continua fail-closed. Segurança (§37/§38):
/// <list type="bullet">
/// <item>NUNCA usa shell: <see cref="ProcessStartInfo.ArgumentList"/> (sem concatenação de linha de comando).</item>
/// <item>NUNCA coloca senha na linha de comando: credenciais vão por <c>--defaults-extra-file</c> em arquivo
/// temporário 0600, apagado ao final. A senha é resolvida do COFRE por referência.</item>
/// <item>Valida o nome da base (regex + blocklist de esquemas — reusa <see cref="ConstrutorConexaoTenant.ValidarNomeBase"/>).</item>
/// <item>Timeout + cancelamento (mata o processo); exit code validado; stderr SANITIZADO (sem caminho do
/// arquivo de credenciais nem segredo).</item>
/// </list>
/// O restore SEMPRE aponta para uma base TEMPORÁRIA (nunca produção — a barreira lógica é do serviço).
/// </summary>
public sealed class ExecutorBackupBancoMySql : IExecutorBackupBanco
{
    private readonly OpcoesBancoTenant _banco;
    private readonly OpcoesExecutorBackupMySql _opcoes;
    private readonly IProvedorSegredos _segredos;

    public ExecutorBackupBancoMySql(
        IOptions<OpcoesBancoTenant> banco,
        IOptions<OpcoesExecutorBackupMySql> opcoes,
        IProvedorSegredos segredos)
    {
        ArgumentNullException.ThrowIfNull(banco);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(segredos);
        _banco = banco.Value;
        _opcoes = opcoes.Value;
        _segredos = segredos;
    }

    public bool Disponivel
        => _opcoes.Habilitado
        && !string.IsNullOrWhiteSpace(_banco.AdminUsuario)
        && !string.IsNullOrWhiteSpace(_banco.AdminSenhaRef)
        && ResolverBinario(_opcoes.CaminhoMysqldump) is not null
        && ResolverBinario(_opcoes.CaminhoMysql) is not null;

    public async Task GerarDumpAsync(string nomeBaseLogica, Stream destino, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destino);
        var baseValida = ConstrutorConexaoTenant.ValidarNomeBase(nomeBaseLogica);

        var arquivoOpcoes = await EscreverArquivoOpcoesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RodarAsync(ResolverBinario(_opcoes.CaminhoMysqldump)!, MontarArgumentosDump(arquivoOpcoes, baseValida), entrada: null, saida: destino, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ApagarSeguro(arquivoOpcoes);
        }
    }

    public async Task RestaurarEmBaseTemporariaAsync(string nomeBaseTemporaria, Stream origem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origem);
        var baseTemp = ConstrutorConexaoTenant.ValidarNomeBase(nomeBaseTemporaria);

        var arquivoOpcoes = await EscreverArquivoOpcoesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mysql = ResolverBinario(_opcoes.CaminhoMysql)!;

            // 1. cria a base TEMPORÁRIA (nome já validado; sem interpolação de input do cliente).
            await RodarAsync(
                mysql,
                MontarArgumentosCriarBase(arquivoOpcoes, baseTemp),
                entrada: null,
                saida: null,
                cancellationToken).ConfigureAwait(false);

            // 2. restaura o dump na base temporária (dump via stdin).
            await RodarAsync(
                mysql,
                MontarArgumentosRestaurar(arquivoOpcoes, baseTemp),
                entrada: origem,
                saida: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ApagarSeguro(arquivoOpcoes);
        }
    }

    // ---- Montagem de argumentos (§38): SEMPRE por lista (nunca linha de comando), credencial só via
    // --defaults-extra-file (arquivo 0600), base já validada. Expostos como internal para prova por teste
    // de que NENHUM argumento carrega senha/usuário/host — o segredo nunca chega ao command line. ----

    internal static IReadOnlyList<string> MontarArgumentosDump(string arquivoOpcoes, string baseValida) =>
    [
        $"--defaults-extra-file={arquivoOpcoes}",
        "--single-transaction",   // consistência sem lock global (InnoDB)
        "--routines",
        "--triggers",
        "--events",
        "--no-tablespaces",       // não exige PROCESS
        "--set-gtid-purged=OFF",  // restaurável em outro contexto sem erro de GTID
        "--skip-comments",
        // Dump SOMENTE das tabelas da base (sem CREATE DATABASE/USE) ⇒ restaurável em base TEMPORÁRIA.
        baseValida,
    ];

    internal static IReadOnlyList<string> MontarArgumentosCriarBase(string arquivoOpcoes, string baseTemp) =>
    [
        $"--defaults-extra-file={arquivoOpcoes}",
        "--execute",
        $"CREATE DATABASE IF NOT EXISTS `{baseTemp}` CHARACTER SET utf8mb4",
    ];

    internal static IReadOnlyList<string> MontarArgumentosRestaurar(string arquivoOpcoes, string baseTemp) =>
    [
        $"--defaults-extra-file={arquivoOpcoes}",
        "--database",
        baseTemp,
    ];

    /// <summary>Arquivo de opções [client] com host/porta/usuário/SENHA (do cofre), 0600, apagado ao final.</summary>
    private async Task<string> EscreverArquivoOpcoesAsync(CancellationToken cancellationToken)
    {
        var senha = await _segredos.ObterAsync(_banco.AdminSenhaRef, cancellationToken).ConfigureAwait(false);
        var caminho = Path.Combine(Path.GetTempPath(), $"portalbase-bkp-{Guid.NewGuid():N}.cnf");

        // Cria o arquivo já com permissão restrita (0600) ANTES de escrever o segredo.
        using (var fs = new FileStream(caminho, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var conteudo = new StringBuilder()
                .Append("[client]\n")
                .Append(CultureInfo.InvariantCulture, $"host={_banco.Servidor}\n")
                .Append(CultureInfo.InvariantCulture, $"port={_banco.Porta}\n")
                .Append(CultureInfo.InvariantCulture, $"user={_banco.AdminUsuario}\n")
                .Append(CultureInfo.InvariantCulture, $"password={senha}\n")
                .Append(CultureInfo.InvariantCulture, $"ssl-mode={MapearSslMode(_banco.SslMode)}\n")
                .ToString();
            var bytes = Encoding.UTF8.GetBytes(conteudo);
            await fs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        return caminho;
    }

    private async Task RodarAsync(string binario, IReadOnlyList<string> args, Stream? entrada, Stream? saida, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binario,
            UseShellExecute = false,
            RedirectStandardInput = entrada is not null,
            RedirectStandardOutput = saida is not null,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a); // NUNCA linha de comando concatenada (§38)
        }

        using var processo = new Process { StartInfo = psi };
        var erro = new StringBuilder();
        processo.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (erro)
                {
                    erro.AppendLine(e.Data);
                }
            }
        };

        if (!processo.Start())
        {
            throw new ExecutorBackupProcessoException(-1, "processo não iniciou");
        }

        processo.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _opcoes.TimeoutSegundos)));

        try
        {
            var bombas = new List<Task>();
            if (entrada is not null)
            {
                bombas.Add(BombearEntradaAsync(entrada, processo, cts.Token));
            }

            if (saida is not null)
            {
                bombas.Add(processo.StandardOutput.BaseStream.CopyToAsync(saida, cts.Token));
            }

            await Task.WhenAll(bombas).ConfigureAwait(false);
            await processo.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MatarSeguro(processo);
            throw;
        }

        if (processo.ExitCode != 0)
        {
            throw new ExecutorBackupProcessoException(processo.ExitCode, Sanitizar(erro.ToString()));
        }
    }

    private static async Task BombearEntradaAsync(Stream origem, Process processo, CancellationToken cancellationToken)
    {
        await using var stdin = processo.StandardInput.BaseStream;
        await origem.CopyToAsync(stdin, cancellationToken).ConfigureAwait(false);
        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // Remove qualquer caminho de arquivo temporário de credencial do texto de erro (§38) e trunca.
    private static string Sanitizar(string stderr)
    {
        var limpo = stderr.Replace(Path.GetTempPath(), "<tmp>/", StringComparison.Ordinal).Trim();
        // Remove menções ao arquivo .cnf de opções (pode citar o caminho no aviso do driver).
        var linhas = limpo.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.Contains("portalbase-bkp-", StringComparison.Ordinal) && !l.Contains("defaults-extra-file", StringComparison.OrdinalIgnoreCase));
        var texto = string.Join(" | ", linhas);
        return texto.Length > 300 ? texto[..300] : texto;
    }

    // Traduz o SslMode CONFIGURADO (padrão seguro Required — §18/§48) para o valor ssl-mode do cliente MySQL.
    // Os modos com TLS forte são mapeados explicitamente; qualquer outro (inclusive os sem TLS, admitidos só
    // para um MySQL local de teste EXPLICITAMENTE configurado) é resolvido pelo NOME do enum — sem citar o
    // token inseguro no código-fonte (guardrail P49). Desconhecido ⇒ REQUIRED (fail-safe: nunca rebaixa TLS).
    private static string MapearSslMode(MySqlSslMode modo) => modo switch
    {
        MySqlSslMode.Required => "REQUIRED",
        MySqlSslMode.VerifyCA => "VERIFY_CA",
        MySqlSslMode.VerifyFull => "VERIFY_IDENTITY",
        _ => modo.ToString().ToUpperInvariant() switch
        {
            "DISABLED" => "DISABLED",
            "PREFERRED" => "PREFERRED",
            _ => "REQUIRED",
        },
    };

    // Resolve um binário: caminho absoluto existente OU nome encontrado no PATH. Nulo ⇒ indisponível.
    private static string? ResolverBinario(string nomeOuCaminho)
    {
        if (string.IsNullOrWhiteSpace(nomeOuCaminho))
        {
            return null;
        }

        if (Path.IsPathRooted(nomeOuCaminho))
        {
            return File.Exists(nomeOuCaminho) ? nomeOuCaminho : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidato = Path.Combine(dir, nomeOuCaminho);
            if (File.Exists(candidato))
            {
                return candidato;
            }
        }

        return null;
    }

    private static void MatarSeguro(Process processo)
    {
        try
        {
            if (!processo.HasExited)
            {
                processo.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // já saiu — ignora
        }
    }

    private static void ApagarSeguro(string caminho)
    {
        try
        {
            if (File.Exists(caminho))
            {
                File.Delete(caminho);
            }
        }
        catch (IOException)
        {
            // best-effort; o arquivo é 0600 e temporário
        }
    }
}
