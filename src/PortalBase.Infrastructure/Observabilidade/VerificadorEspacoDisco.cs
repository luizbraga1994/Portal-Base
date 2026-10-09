using Microsoft.Extensions.Configuration;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>
/// Espaço livre dos discos das pastas do portal (OBS-004): arquivos enviados, temporários, backups, cópia externa
/// (quando é um disco local) e logs. Cada pasta é ligada ao disco/ponto de montagem que a contém (o de caminho mais
/// longo); pastas no mesmo disco viram um registro só. Pasta de rede (<c>\\servidor\...</c>) fica de fora: não dá para
/// medir sem acessar o servidor remoto (o próprio backup acusa falha da cópia externa). Só lê tamanhos — nunca o
/// conteúdo — e não expõe o caminho, só a unidade e o uso.
/// </summary>
public sealed class VerificadorEspacoDisco : IVerificadorEspacoDisco
{
    private readonly List<(string Uso, string Diretorio)> _pastas;
    private readonly Func<IReadOnlyList<DriveInfo>> _unidades;

    public VerificadorEspacoDisco(IEnumerable<(string Uso, string? Diretorio)> pastas, Func<IReadOnlyList<DriveInfo>>? unidades = null)
    {
        ArgumentNullException.ThrowIfNull(pastas);
        _pastas = pastas
            .Where(p => !string.IsNullOrWhiteSpace(p.Diretorio))
            .Select(p => (p.Uso, p.Diretorio!.Trim()))
            .ToList();
        _unidades = unidades ?? (() => DriveInfo.GetDrives());
    }

    /// <summary>As pastas configuradas nas mesmas chaves que o <c>scripts/configurar-iis.ps1</c> grava.</summary>
    public static VerificadorEspacoDisco DaConfiguracao(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        return new VerificadorEspacoDisco(
        [
            ("arquivos", configuracao["Armazenamento:DiretorioRaiz"]),
            ("temporários", configuracao["Armazenamento:DiretorioTemporario"]),
            ("backups", configuracao["ArmazenamentoBackup:DiretorioRaiz"]),
            ("cópia externa", configuracao["ArmazenamentoBackup:DiretorioOffsite"]),
            ("logs", configuracao[OpcoesLogArquivo.Secao + ":Diretorio"]),
        ]);
    }

    public IReadOnlyList<EspacoDisco> Medir()
    {
        if (_pastas.Count == 0)
        {
            return [];
        }

        var comparacao = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var unidades = _unidades().Where(Pronta).ToList();
        var usosPorUnidade = new Dictionary<DriveInfo, List<string>>();
        foreach (var (uso, diretorio) in _pastas)
        {
            if (!TentarCaminhoLocal(diretorio, out var completo))
            {
                continue;
            }

            var unidade = unidades
                .Where(u => ComoDiretorio(completo).StartsWith(ComoDiretorio(u.RootDirectory.FullName), comparacao))
                .MaxBy(u => u.RootDirectory.FullName.Length);
            if (unidade is null)
            {
                continue;
            }

            if (!usosPorUnidade.TryGetValue(unidade, out var usos))
            {
                usosPorUnidade[unidade] = usos = [];
            }

            if (!usos.Contains(uso, StringComparer.Ordinal))
            {
                usos.Add(uso);
            }
        }

        var resultado = new List<EspacoDisco>();
        foreach (var (unidade, usos) in usosPorUnidade)
        {
            try
            {
                resultado.Add(new EspacoDisco(unidade.Name, string.Join(", ", usos), unidade.AvailableFreeSpace, unidade.TotalSize));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disco que sumiu entre a listagem e a leitura: fica de fora desta volta.
            }
        }

        return resultado;
    }

    private static bool TentarCaminhoLocal(string diretorio, out string completo)
    {
        completo = string.Empty;
        if (diretorio.StartsWith(@"\\", StringComparison.Ordinal) || diretorio.StartsWith("//", StringComparison.Ordinal))
        {
            return false; // pasta de rede: não medível daqui
        }

        try
        {
            completo = Path.GetFullPath(diretorio);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool Pronta(DriveInfo unidade)
    {
        try
        {
            return unidade.IsReady && unidade.TotalSize > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ComoDiretorio(string caminho)
        => Path.EndsInDirectorySeparator(caminho) ? caminho : caminho + Path.DirectorySeparatorChar;
}
