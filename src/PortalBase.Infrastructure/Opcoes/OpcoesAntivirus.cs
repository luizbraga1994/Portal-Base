namespace PortalBase.Infrastructure.Opcoes;

/// <summary>Motor de antivírus usado na varredura dos arquivos enviados.</summary>
public enum MotorAntivirus
{
    /// <summary>Windows Defender quando o servidor é Windows; nos demais sistemas, nenhum (padrão).</summary>
    Automatico = 0,

    /// <summary>Sem motor: a varredura fica indisponível e o upload é rejeitado enquanto o antivírus for obrigatório.</summary>
    Nenhum = 1,

    /// <summary>Microsoft Defender Antivirus pela linha de comando (<c>MpCmdRun.exe</c>).</summary>
    WindowsDefender = 2,
}

/// <summary>
/// Opções do antivírus dos uploads (seção <c>Antivirus</c>). Nenhuma delas é segredo. Sem configuração, o servidor
/// Windows usa o Windows Defender e os demais ficam sem motor (o upload segue a política
/// <c>Upload:AntivirusObrigatorio</c> — fail-closed).
/// </summary>
public sealed class OpcoesAntivirus
{
    public const string Secao = "Antivirus";

    /// <summary><c>Automatico</c> (padrão), <c>WindowsDefender</c> ou <c>Nenhum</c>.</summary>
    public string Motor { get; set; } = nameof(MotorAntivirus.Automatico);

    /// <summary>
    /// Caminho ABSOLUTO do <c>MpCmdRun.exe</c>. Vazio (padrão) procura a versão mais nova em
    /// <c>%ProgramData%\Microsoft\Windows Defender\Platform</c> e, sem ela, <c>%ProgramFiles%\Windows Defender</c>.
    /// </summary>
    public string? CaminhoMpCmdRun { get; set; }

    /// <summary>Tempo máximo de UMA varredura (segundos). Estourado, a varredura conta como falha (nunca "limpo").</summary>
    public int TimeoutSegundos { get; set; } = 120;

    /// <summary>Tentativas quando o Defender responde com erro (não vale para tempo esgotado nem cancelamento).</summary>
    public int Tentativas { get; set; } = 2;

    /// <summary>Varreduras simultâneas no máximo (cada uma é um processo do Defender).</summary>
    public int MaxVarredurasSimultaneas { get; set; } = 2;

    /// <summary>
    /// Interpreta o motor configurado. Valor desconhecido é ERRO de configuração (falha na inicialização), nunca
    /// "sem antivírus" silencioso.
    /// </summary>
    public static MotorAntivirus InterpretarMotor(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return MotorAntivirus.Automatico;
        }

        var texto = valor.Trim();
        if (!int.TryParse(texto, out _) && Enum.TryParse<MotorAntivirus>(texto, ignoreCase: true, out var motor))
        {
            return motor;
        }

        throw new InvalidOperationException(
            $"{Secao}:Motor inválido. Use {nameof(MotorAntivirus.Automatico)}, {nameof(MotorAntivirus.WindowsDefender)} ou {nameof(MotorAntivirus.Nenhum)}.");
    }

    /// <summary>O Windows Defender deve ser usado neste servidor?</summary>
    public static bool UsarWindowsDefender(MotorAntivirus motor, bool servidorWindows) => motor switch
    {
        MotorAntivirus.WindowsDefender => true,
        MotorAntivirus.Automatico => servidorWindows,
        _ => false,
    };
}
