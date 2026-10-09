using System.Xml;

namespace PortalBase.Application.Seguranca;

/// <summary>
/// Configuração SEGURA para leitura de XML (Prompt 07, seção 17). Bloqueia XXE e expansão
/// explosiva: DTD proibido, SEM resolução de entidades externas (nenhum acesso a rede/disco no
/// parsing) e limites de tamanho/entidades. Todo processamento de XML DEVE usar
/// estes settings. Este prompt apenas prepara o controle — não implementa geradores fiscais.
/// </summary>
public static class XmlSeguro
{
    private const long MaxCaracteresDocumento = 50_000_000; // ~50 MB de texto

    /// <summary>Settings endurecidos: sem DTD, sem resolver externo, com limites.</summary>
    public static XmlReaderSettings Configuracoes() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,   // bloqueia DTD (vetor de XXE)
        XmlResolver = null,                        // não resolve entidades externas (rede/disco)
        MaxCharactersFromEntities = 0,             // sem expansão de entidades
        MaxCharactersInDocument = MaxCaracteresDocumento,
        CloseInput = true,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
    };

    public static XmlReader CriarLeitor(Stream entrada) => XmlReader.Create(entrada, Configuracoes());

    public static XmlReader CriarLeitor(TextReader entrada) => XmlReader.Create(entrada, Configuracoes());
}
