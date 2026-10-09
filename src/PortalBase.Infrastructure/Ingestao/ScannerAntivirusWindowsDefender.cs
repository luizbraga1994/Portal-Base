using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortalBase.Application.Ingestao;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>
/// Antivírus dos uploads pelo Microsoft Defender Antivirus (Windows Defender), com a ferramenta de linha de comando
/// <c>MpCmdRun.exe -Scan -ScanType 3 -File &lt;arquivo&gt; -DisableRemediation</c>:
/// <list type="bullet">
/// <item>O conteúdo é copiado para um arquivo PRIVADO na área temporária (a cópia de trabalho do upload fica aberta com
/// acesso exclusivo), varrido e apagado — sempre, inclusive em falha ou cancelamento.</item>
/// <item><c>-DisableRemediation</c>: o Defender só informa (não move o arquivo nem aplica exclusões); quem decide é o
/// caso de uso.</item>
/// <item>FAIL-CLOSED: só o código 0 é "limpo". Código 2 é ameaça (a menos que a saída mostre uma falha da ferramenta);
/// qualquer outro código, tempo esgotado ou falha ao iniciar é ERRO — o upload é rejeitado, nunca aceito.</item>
/// <item>Tempo máximo por varredura, nova tentativa controlada só para erro da ferramenta, limite de varreduras
/// simultâneas e <see cref="CancellationToken"/> do pedido (regra 11).</item>
/// <item>Nada do conteúdo nem do nome do arquivo vai para log; só o código de saída e o código de erro do Defender.</item>
/// </list>
/// </summary>
public sealed partial class ScannerAntivirusWindowsDefender : IScannerAntivirus, IDisposable
{
    /// <summary>Código de saída do MpCmdRun sem ameaças.</summary>
    public const int CodigoSemAmeaca = 0;

    /// <summary>Código de saída do MpCmdRun com ameaça encontrada (com <c>-DisableRemediation</c>, nada é removido).</summary>
    public const int CodigoAmeacaEncontrada = 2;

    /// <summary>Idade a partir da qual uma cópia de varredura é considerada órfã (processo caiu no meio da varredura).</summary>
    public static readonly TimeSpan IdadeCopiaOrfa = TimeSpan.FromHours(1);

    private const int ErroArquivoComVirus = unchecked((int)0x800700E1); // ERROR_VIRUS_INFECTED
    private const int ErroArquivoComVirusRemovido = unchecked((int)0x800700E2); // ERROR_VIRUS_DELETED

    private readonly IExecutorProcessoExterno _executor;
    private readonly ILogger _logger;
    private readonly Func<string?> _localizar;
    private readonly string _diretorio;
    private readonly TimeSpan _timeout;
    private readonly int _tentativas;
    private readonly TimeSpan _esperaEntreTentativas;
    private readonly SemaphoreSlim _vagas;

    public ScannerAntivirusWindowsDefender(
        IOptions<OpcoesAntivirus> opcoes,
        IOptions<OpcoesArmazenamento> armazenamento,
        IExecutorProcessoExterno executor,
        ILogger<ScannerAntivirusWindowsDefender> logger)
        : this(opcoes, armazenamento, executor, logger, localizar: null, esperaEntreTentativas: TimeSpan.FromSeconds(2))
    {
    }

    /// <summary>Construtor com o localizador e a espera entre tentativas substituíveis (testes).</summary>
    internal ScannerAntivirusWindowsDefender(
        IOptions<OpcoesAntivirus> opcoes,
        IOptions<OpcoesArmazenamento> armazenamento,
        IExecutorProcessoExterno executor,
        ILogger logger,
        Func<string?>? localizar,
        TimeSpan esperaEntreTentativas)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(armazenamento);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);

        var o = opcoes.Value;
        _executor = executor;
        _logger = logger;
        var configurado = o.CaminhoMpCmdRun;
        _localizar = localizar ?? (() => LocalizadorMpCmdRun.Localizar(configurado));
        _timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSegundos, 5, 1800));
        _tentativas = Math.Clamp(o.Tentativas, 1, 5);
        _esperaEntreTentativas = esperaEntreTentativas < TimeSpan.Zero ? TimeSpan.Zero : esperaEntreTentativas;
        _vagas = new SemaphoreSlim(Math.Clamp(o.MaxVarredurasSimultaneas, 1, 16));

        var temporario = armazenamento.Value.DiretorioTemporario;
        var raiz = string.IsNullOrWhiteSpace(temporario)
            ? Path.Combine(Path.GetTempPath(), "portalbase-tmp")
            : Path.GetFullPath(temporario);
        _diretorio = Path.Combine(raiz, "antivirus");
        RemoverCopiasOrfas();

        if (_localizar() is null)
        {
            LogDefenderAusente(_logger);
        }
    }

    /// <summary>Pasta das cópias temporárias da varredura.</summary>
    public string DiretorioVarredura => _diretorio;

    /// <summary>Há um <c>MpCmdRun.exe</c> neste servidor (ou no caminho configurado)?</summary>
    public bool Disponivel => _localizar() is not null;

    public async Task<ResultadoAntivirus> EscanearAsync(Stream conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        await _vagas.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executavel = _localizar();
            if (executavel is null)
            {
                LogDefenderAusente(_logger);
                return ResultadoAntivirus.Indisponivel;
            }

            var caminho = Path.Combine(_diretorio, $"{Guid.NewGuid():N}.bin");
            try
            {
                if (await GravarCopiaAsync(conteudo, caminho, cancellationToken).ConfigureAwait(false) is ResultadoAntivirus naGravacao)
                {
                    return naGravacao;
                }

                return await VarrerAsync(executavel, caminho, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                RemoverCopia(caminho);
            }
        }
        finally
        {
            _vagas.Release();
        }
    }

    /// <summary>
    /// Traduz a resposta do MpCmdRun. Só "0" sem relato de ameaça é limpo; "2" é ameaça, salvo quando a saída mostra uma
    /// falha da própria ferramenta (<c>CmdTool: Failed with hr = 0x...</c>) — aí é erro (o arquivo é rejeitado do mesmo
    /// jeito, mas sem ser rotulado como infectado sem evidência).
    /// </summary>
    public static ResultadoAntivirus Classificar(ResultadoProcessoExterno resposta)
    {
        ArgumentNullException.ThrowIfNull(resposta);

        var saida = resposta.Saida ?? string.Empty;
        var falhaDaFerramenta = FalhaDaFerramenta().IsMatch(saida);
        if (!falhaDaFerramenta && AmeacasEncontradas().IsMatch(saida))
        {
            return ResultadoAntivirus.Infectado;
        }

        return resposta.CodigoSaida switch
        {
            CodigoSemAmeaca when !falhaDaFerramenta => ResultadoAntivirus.Limpo,
            CodigoAmeacaEncontrada when !falhaDaFerramenta => ResultadoAntivirus.Infectado,
            _ => ResultadoAntivirus.Erro,
        };
    }

    public void Dispose() => _vagas.Dispose();

    private async Task<ResultadoAntivirus?> GravarCopiaAsync(Stream conteudo, string caminho, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_diretorio);
            if (conteudo.CanSeek)
            {
                conteudo.Position = 0;
            }

            // FileShare.Read: o Defender precisa ler a cópia; ninguém mais escreve nela.
            await using (var destino = new FileStream(
                caminho, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous))
            {
                await conteudo.CopyToAsync(destino, cancellationToken).ConfigureAwait(false);
                await destino.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (IOException ex) when (ex.HResult is ErroArquivoComVirus or ErroArquivoComVirusRemovido)
        {
            // A proteção em tempo real do Defender barrou a própria gravação: é a detecção do Defender.
            LogAmeacaDetectada(_logger);
            return ResultadoAntivirus.Infectado;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFalhaCopia(_logger, ex.GetType().Name);
            return ResultadoAntivirus.Erro;
        }
    }

    private async Task<ResultadoAntivirus> VarrerAsync(string executavel, string caminho, CancellationToken cancellationToken)
    {
        string[] argumentos = ["-Scan", "-ScanType", "3", "-File", caminho, "-DisableRemediation"];

        for (var tentativa = 1; ; tentativa++)
        {
            string codigo;
            string detalhe;
            try
            {
                var resposta = await _executor.ExecutarAsync(executavel, argumentos, _timeout, cancellationToken).ConfigureAwait(false);
                var resultado = Classificar(resposta);
                if (resultado == ResultadoAntivirus.Infectado)
                {
                    LogAmeacaDetectada(_logger);
                }

                if (resultado != ResultadoAntivirus.Erro)
                {
                    return resultado;
                }

                codigo = resposta.CodigoSaida.ToString(CultureInfo.InvariantCulture);
                detalhe = CodigoHr().Match(resposta.Saida ?? string.Empty) is { Success: true } hr ? hr.Value : "sem código";
            }
            catch (TimeoutException)
            {
                // Uma nova tentativa dobraria a espera do usuário: tempo esgotado já é a resposta (fail-closed).
                LogTempoEsgotado(_logger, (int)_timeout.TotalSeconds);
                return ResultadoAntivirus.Erro;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                codigo = "não iniciou";
                detalhe = ex.GetType().Name;
            }

            if (tentativa >= _tentativas)
            {
                LogFalhaFinal(_logger, tentativa, codigo, detalhe);
                return ResultadoAntivirus.Erro;
            }

            LogFalhaTentativa(_logger, tentativa, codigo, detalhe);
            if (_esperaEntreTentativas > TimeSpan.Zero)
            {
                await Task.Delay(_esperaEntreTentativas, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Cópias deixadas por um processo que caiu no meio da varredura contêm dados do contribuinte: são apagadas na
    // inicialização. Só as antigas — uma varredura em andamento em outro processo nunca dura uma hora.
    private void RemoverCopiasOrfas()
    {
        try
        {
            if (!Directory.Exists(_diretorio))
            {
                return;
            }

            var limite = DateTime.UtcNow - IdadeCopiaOrfa;
            foreach (var arquivo in Directory.EnumerateFiles(_diretorio, "*.bin"))
            {
                if (File.GetLastWriteTimeUtc(arquivo) < limite)
                {
                    RemoverCopia(arquivo);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFalhaRemocao(_logger, ex.GetType().Name);
        }
    }

    private void RemoverCopia(string caminho)
    {
        try
        {
            File.Delete(caminho);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFalhaRemocao(_logger, ex.GetType().Name);
        }
    }

    [GeneratedRegex(@"CmdTool:\s*Failed|hr\s*=\s*0x[0-9a-f]{8}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FalhaDaFerramenta();

    [GeneratedRegex(@"found\s+[1-9][0-9]*\s+threats?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AmeacasEncontradas();

    [GeneratedRegex(@"0x[0-9a-f]{8}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodigoHr();

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Windows Defender (MpCmdRun.exe) não encontrado: os arquivos enviados são rejeitados enquanto Upload:AntivirusObrigatorio=true. Verifique se o Microsoft Defender Antivirus está instalado e ativo neste servidor ou informe Antivirus:CaminhoMpCmdRun.")]
    private static partial void LogDefenderAusente(ILogger logger);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning, Message = "Windows Defender: a varredura falhou na tentativa {Tentativa} (código de saída {Codigo}, {Detalhe}); tentando de novo.")]
    private static partial void LogFalhaTentativa(ILogger logger, int tentativa, string codigo, string detalhe);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Error, Message = "Windows Defender: a varredura falhou após {Tentativas} tentativa(s) (código de saída {Codigo}, {Detalhe}); o arquivo enviado foi rejeitado. Verifique o Microsoft Defender Antivirus neste servidor.")]
    private static partial void LogFalhaFinal(ILogger logger, int tentativas, string codigo, string detalhe);

    [LoggerMessage(EventId = 7204, Level = LogLevel.Error, Message = "Windows Defender: a varredura não terminou em {Segundos} s; o arquivo enviado foi rejeitado.")]
    private static partial void LogTempoEsgotado(ILogger logger, int segundos);

    [LoggerMessage(EventId = 7205, Level = LogLevel.Warning, Message = "Windows Defender: ameaça detectada em um arquivo enviado; o arquivo foi rejeitado (detalhes na auditoria e no histórico de proteção do Defender).")]
    private static partial void LogAmeacaDetectada(ILogger logger);

    [LoggerMessage(EventId = 7206, Level = LogLevel.Error, Message = "Windows Defender: não foi possível gravar a cópia temporária para a varredura ({TipoErro}); o arquivo enviado foi rejeitado. Verifique a permissão de escrita em Armazenamento:DiretorioTemporario.")]
    private static partial void LogFalhaCopia(ILogger logger, string tipoErro);

    [LoggerMessage(EventId = 7207, Level = LogLevel.Warning, Message = "Windows Defender: a cópia temporária da varredura não pôde ser apagada ({TipoErro}).")]
    private static partial void LogFalhaRemocao(ILogger logger, string tipoErro);
}
