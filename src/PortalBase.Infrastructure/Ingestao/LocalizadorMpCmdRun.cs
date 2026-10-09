using System.Globalization;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>
/// Localiza o <c>MpCmdRun.exe</c> do Microsoft Defender Antivirus. A procura é refeita a cada varredura: as
/// atualizações da plataforma do Defender instalam uma pasta nova e removem a antiga, e um caminho guardado na
/// inicialização deixaria de existir.
/// </summary>
public static class LocalizadorMpCmdRun
{
    public const string NomeExecutavel = "MpCmdRun.exe";

    /// <summary>Caminho configurado (absoluto) ou a instalação do Defender neste servidor; <c>null</c> se não houver.</summary>
    public static string? Localizar(string? configurado) => Localizar(
        configurado,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    /// <summary>
    /// Ordem: (1) caminho configurado — só ele, sem procurar outro se não existir; (2) a versão mais nova em
    /// <c>{ProgramData}\Microsoft\Windows Defender\Platform\{versão}</c>; (3) <c>{ProgramFiles}\Windows Defender</c>.
    /// </summary>
    public static string? Localizar(string? configurado, string? programData, string? programFiles)
    {
        if (!string.IsNullOrWhiteSpace(configurado))
        {
            var caminho = configurado.Trim();
            return Path.IsPathFullyQualified(caminho) && File.Exists(caminho) ? Path.GetFullPath(caminho) : null;
        }

        if (!string.IsNullOrWhiteSpace(programData) && MaisNovoDaPlataforma(programData) is string daPlataforma)
        {
            return daPlataforma;
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var classico = Path.Combine(programFiles, "Windows Defender", NomeExecutavel);
            if (File.Exists(classico))
            {
                return classico;
            }
        }

        return null;
    }

    /// <summary>Versão de uma pasta da plataforma (ex.: <c>4.18.24090.11-0</c>); <c>null</c> se o nome não for uma versão.</summary>
    public static (Version Versao, int Sufixo)? VersaoDaPasta(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
        {
            return null;
        }

        var partes = nome.Split('-', 2);
        if (!Version.TryParse(partes[0], out var versao))
        {
            return null;
        }

        var sufixo = 0;
        if (partes.Length == 2 && !int.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out sufixo))
        {
            return null;
        }

        return (versao, sufixo);
    }

    private static string? MaisNovoDaPlataforma(string programData)
    {
        var plataforma = Path.Combine(programData, "Microsoft", "Windows Defender", "Platform");
        try
        {
            if (!Directory.Exists(plataforma))
            {
                return null;
            }

            return Directory.EnumerateDirectories(plataforma)
                .Select(pasta => (Caminho: Path.Combine(pasta, NomeExecutavel), Versao: VersaoDaPasta(Path.GetFileName(pasta))))
                .Where(x => x.Versao is not null && File.Exists(x.Caminho))
                .OrderByDescending(x => x.Versao!.Value.Versao)
                .ThenByDescending(x => x.Versao!.Value.Sufixo)
                .Select(x => x.Caminho)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem acesso à pasta da plataforma (ACL do Defender): fica com o caminho clássico.
            return null;
        }
    }
}
