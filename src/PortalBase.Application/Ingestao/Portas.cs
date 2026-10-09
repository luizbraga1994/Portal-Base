using PortalBase.Domain.Arquivos;

namespace PortalBase.Application.Ingestao;

/// <summary>Resultado da varredura antivírus.</summary>
public enum ResultadoAntivirus
{
    Limpo = 1,
    Infectado = 2,
    Indisponivel = 3,
    Erro = 4,
}

/// <summary>
/// Scanner antivírus (Prompt 08, seção 14). Se o scanner for OBRIGATÓRIO no ambiente e estiver
/// indisponível, o caso de uso REJEITA (fail-closed) — nunca "considera limpo". NUNCA registra o
/// conteúdo do arquivo.
/// </summary>
public interface IScannerAntivirus
{
    bool Disponivel { get; }

    Task<ResultadoAntivirus> EscanearAsync(Stream conteudo, CancellationToken cancellationToken = default);
}

/// <summary>Resultado da validação de CONTEÚDO real (magic bytes / estrutura), independente da extensão.</summary>
public sealed record ResultadoValidacaoConteudo(bool Valido, string ContentTypeDetectado, string? Motivo)
{
    public static ResultadoValidacaoConteudo Ok(string contentType) => new(true, contentType, null);

    public static ResultadoValidacaoConteudo Invalido(string motivo) => new(false, "application/octet-stream", motivo);
}

/// <summary>
/// Validação do CONTEÚDO real do arquivo (seções 10, 11, 12): assinatura (magic bytes) coerente
/// com a extensão e, para formatos ZIP-based (OpenXML), proteção contra ZIP bomb, excesso de
/// entradas e Zip Slip. O stream é lido do início (seekable) e reposicionado.
/// </summary>
public interface IValidadorConteudoArquivo
{
    Task<ResultadoValidacaoConteudo> ValidarAsync(Stream conteudo, string extensao, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repositório de metadados do arquivo NO BANCO DO TENANT (seções 18, 19). O conteúdo nunca
/// passa por aqui. A idempotência é tenant-scoped: cada banco de tenant tem seu próprio conjunto.
/// </summary>
public interface IRepositorioArquivoImportacao
{
    Task<bool> ExisteComHashAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Id do arquivo já recebido com este hash no tenant (idempotência do envio), ou nulo.</summary>
    Task<Guid?> ObterIdPorHashAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Arquivo do tenant atual (rastreado, para transição de estado), ou nulo.</summary>
    Task<ArquivoImportacao?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AdicionarAsync(ArquivoImportacao arquivo, CancellationToken cancellationToken = default);

    Task SalvarAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Salva com controle otimista: <c>false</c> quando outra operação alterou o arquivo antes (o estado REAL é
    /// recarregado) — é a "reivindicação" do processamento, que impede processar o mesmo arquivo duas vezes.
    /// </summary>
    Task<bool> TentarSalvarAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Área de trabalho TEMPORÁRIA privada (seção 13): fornece um stream seekable (arquivo temporário
/// fora de wwwroot, nome aleatório, excluído ao descartar) para que o caso de uso faça hash,
/// validação e varredura em passes sem carregar tudo em memória (seção 34).
/// </summary>
public interface IAreaTrabalhoTemporaria
{
    Task<Stream> CriarAsync(CancellationToken cancellationToken = default);
}
