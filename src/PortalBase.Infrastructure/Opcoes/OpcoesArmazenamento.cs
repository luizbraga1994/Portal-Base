namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Configuração do ARMAZENAMENTO privado de arquivos (Prompt 08, seções 13, 24, 26). O binário
/// vai para um diretório PRIVADO fora de <c>wwwroot</c> (nunca servido estaticamente). Em
/// Produção o diretório raiz é obrigatório (fail-closed) — validado na composição de DI.
/// Nenhum segredo aqui.
/// </summary>
public sealed class OpcoesArmazenamento
{
    public const string Secao = "Armazenamento";

    /// <summary>
    /// Diretório RAIZ privado dos arquivos ingeridos (por tenant, em subpastas). Deve ficar
    /// FORA de wwwroot. Vazio em Dev/Test cai para um diretório temporário local; em Produção,
    /// é obrigatório (a composição de DI falha o startup se ausente).
    /// </summary>
    public string DiretorioRaiz { get; set; } = string.Empty;

    /// <summary>
    /// Diretório de trabalho TEMPORÁRIO (hash/validação/scan em passes). Vazio cai para
    /// uma subpasta do temp do SO. Os arquivos temporários são excluídos ao fechar.
    /// </summary>
    public string DiretorioTemporario { get; set; } = string.Empty;
}
