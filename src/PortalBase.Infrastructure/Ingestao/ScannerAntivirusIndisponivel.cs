using PortalBase.Application.Ingestao;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>
/// Implementação PADRÃO (fail-closed) do scanner antivírus (Prompt 08, seção 14): NÃO há motor
/// de varredura integrado neste ambiente, portanto <see cref="Disponivel"/> é <c>false</c>. Como
/// o antivírus é obrigatório por padrão (<c>Upload:AntivirusObrigatorio</c>), o caso de uso
/// REJEITA o upload quando o scanner está indisponível — NUNCA "considera limpo". A integração
/// com um motor real (ex.: ICAP/ClamAV) é substituída por DI sem alterar o caso de uso.
/// </summary>
public sealed class ScannerAntivirusIndisponivel : IScannerAntivirus
{
    public bool Disponivel => false;

    public Task<ResultadoAntivirus> EscanearAsync(Stream conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        // Sem motor disponível: reporta indisponibilidade. Jamais retorna "Limpo" por omissão.
        return Task.FromResult(ResultadoAntivirus.Indisponivel);
    }
}
