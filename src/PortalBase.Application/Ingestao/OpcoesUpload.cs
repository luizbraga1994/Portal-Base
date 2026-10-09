namespace PortalBase.Application.Ingestao;

/// <summary>
/// Configuração da ingestão de arquivos (Prompt 08, seções 7, 8, 9, 12). Define os limites de
/// tamanho, a allowlist de extensões, a obrigatoriedade do antivírus e as proteções contra
/// ZIP bomb/entradas excessivas para formatos baseados em ZIP (OpenXML). Nenhum valor aqui é
/// segredo. Os padrões são conservadores (fail-closed): antivírus obrigatório por padrão.
/// </summary>
public sealed class OpcoesUpload
{
    public const string Secao = "Upload";

    /// <summary>Tamanho máximo do arquivo recebido (bytes). Padrão 20 MB (baseline do Kestrel).</summary>
    public long MaxTamanhoBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// Allowlist de extensões aceitas (sem ponto, minúsculas). A extensão é derivada do nome no
    /// servidor e SEMPRE conferida contra o CONTEÚDO real (magic bytes). Nunca é uma denylist.
    /// </summary>
    public IReadOnlyList<string> ExtensoesPermitidas { get; set; } = ["xlsx", "csv", "xml", "txt"];

    /// <summary>
    /// Se verdadeiro (padrão), um arquivo só é aceito com resultado LIMPO do antivírus. Se o
    /// scanner estiver indisponível ou falhar, o upload é REJEITADO (fail-closed) — nunca
    /// "considerado limpo" (seção 14).
    /// </summary>
    public bool AntivirusObrigatorio { get; set; } = true;

    /// <summary>Máximo de entradas em um contêiner ZIP/OpenXML (defesa contra ZIP bomb — seção 12).</summary>
    public int MaxEntradasZip { get; set; } = 512;

    /// <summary>Tamanho descompactado total máximo de um contêiner ZIP/OpenXML (bytes).</summary>
    public long MaxTamanhoDescompactadoBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Razão máxima de compressão aceita (descompactado/compactado) por entrada.</summary>
    public double RazaoMaximaCompressao { get; set; } = 100d;

    /// <summary>A extensão (derivada no servidor) está na allowlist? Comparação normalizada.</summary>
    public bool ExtensaoPermitida(string? extensao)
    {
        if (string.IsNullOrWhiteSpace(extensao))
        {
            return false;
        }

        var alvo = extensao.Trim().TrimStart('.').ToLowerInvariant();
        if (alvo.Length == 0)
        {
            return false;
        }

        foreach (var permitida in ExtensoesPermitidas)
        {
            if (!string.IsNullOrWhiteSpace(permitida)
                && string.Equals(permitida.Trim().TrimStart('.'), alvo, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
