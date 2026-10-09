using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>Log em ARQUIVO (seção <see cref="Secao"/>): uma pasta por servidor, um arquivo por dia e por host.</summary>
public sealed class OpcoesLogArquivo
{
    public const string Secao = "Observabilidade:LogArquivo";

    /// <summary>Pasta dos arquivos. Vazio ⇒ sem log em arquivo (desenvolvimento e testes). Gravado pelo configurar-iis.ps1.</summary>
    public string? Diretorio { get; set; }

    /// <summary>Por quantos dias os arquivos são guardados (1 a 3650; padrão 30).</summary>
    public int DiasRetencao { get; set; } = 30;

    /// <summary>Tamanho máximo de cada arquivo em MB (1 a 1024; padrão 100). Passou disso, abre outro no mesmo dia.</summary>
    public int TamanhoMaximoArquivoMb { get; set; } = 100;
}

/// <summary>
/// Liga o log em ARQUIVO nos hosts (Web e Worker) — OBS-001 (auditoria de 08/10/2026): em produção (IIS e serviço do
/// Windows) não há console e o Log de Aplicativo só recebe avisos e erros, então o Information — o que aconteceu antes
/// de um erro — se perdia. Um arquivo por dia e por host (<c>PortalBase.Web-20261008.log</c>), compartilhável (o IIS
/// sobrepõe dois processos na reciclagem do pool), com rotação por tamanho e retenção por tempo E por quantidade (para o
/// disco não encher num dia de muito log). Mesmo formato do console; passa pelo mesmo enriquecedor de redação (regra 8).
/// </summary>
public static class LogArquivo
{
    /// <summary>Formato de cada linha (o mesmo do console do appsettings).</summary>
    public const string Modelo =
        "[{Timestamp:o}] [{Level:u3}] {Application} corr={CorrelationId} {SourceContext} {Message:lj}{NewLine}{Exception}";

    /// <summary>Opções lidas da configuração, com os limites aplicados.</summary>
    public static OpcoesLogArquivo Ler(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        var opcoes = configuracao.GetSection(OpcoesLogArquivo.Secao).Get<OpcoesLogArquivo>() ?? new OpcoesLogArquivo();
        opcoes.DiasRetencao = Math.Clamp(opcoes.DiasRetencao, 1, 3650);
        opcoes.TamanhoMaximoArquivoMb = Math.Clamp(opcoes.TamanhoMaximoArquivoMb, 1, 1024);
        opcoes.Diretorio = string.IsNullOrWhiteSpace(opcoes.Diretorio) ? null : opcoes.Diretorio.Trim();
        return opcoes;
    }

    /// <summary>Caminho-modelo do arquivo do host (o Serilog acrescenta a data antes da extensão).</summary>
    public static string CaminhoModelo(string diretorio, string aplicacao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diretorio);
        ArgumentException.ThrowIfNullOrWhiteSpace(aplicacao);
        return Path.Combine(Path.GetFullPath(diretorio), aplicacao + "-.log");
    }

    /// <summary>Acrescenta o arquivo diário do host <paramref name="aplicacao"/> se <see cref="OpcoesLogArquivo.Diretorio"/> estiver configurado.</summary>
    public static LoggerConfiguration GravarEmArquivoSeConfigurado(
        this LoggerConfiguration configuracaoLog, IConfiguration configuracao, string aplicacao)
    {
        ArgumentNullException.ThrowIfNull(configuracaoLog);
        var opcoes = Ler(configuracao);
        if (opcoes.Diretorio is null)
        {
            return configuracaoLog;
        }

        Directory.CreateDirectory(opcoes.Diretorio);
        return configuracaoLog.WriteTo.File(
            CaminhoModelo(opcoes.Diretorio, aplicacao),
            outputTemplate: Modelo,
            formatProvider: CultureInfo.InvariantCulture,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: opcoes.TamanhoMaximoArquivoMb * 1024L * 1024L,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: opcoes.DiasRetencao * 10, // teto de segurança: até 10 arquivos por dia guardado
            retainedFileTimeLimit: TimeSpan.FromDays(opcoes.DiasRetencao),
            shared: true,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
