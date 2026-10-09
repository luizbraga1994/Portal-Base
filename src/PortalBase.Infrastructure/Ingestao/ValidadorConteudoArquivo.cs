using System.IO.Compression;
using System.Xml;
using Microsoft.Extensions.Options;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Seguranca;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>
/// Valida o CONTEÚDO REAL do arquivo (Prompt 08, seções 10, 11, 12), independentemente da
/// extensão/Content-Type informados pelo cliente: confere a assinatura (magic bytes) coerente
/// com a extensão da allowlist e, para contêineres ZIP/OpenXML (xlsx), aplica defesas contra
/// ZIP bomb (razão de compressão, tamanho descompactado), excesso de entradas e Zip Slip
/// (caminhos com traversal). Nunca extrai o conteúdo — inspeciona apenas o diretório central do
/// ZIP. O stream recebido é SEEKABLE e é reposicionado no início; nunca é fechado.
/// </summary>
public sealed class ValidadorConteudoArquivo : IValidadorConteudoArquivo
{
    private const string ContentTypeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string ContentTypeCsv = "text/csv";
    private const string ContentTypeTxt = "text/plain";
    private const string ContentTypeXml = "application/xml";
    private const string EntradaContentTypes = "[Content_Types].xml";

    // Assinaturas de contêiner/binário que NUNCA são texto (para rejeitar csv/txt/xml disfarçados).
    private static readonly byte[] MagicZip = [0x50, 0x4B, 0x03, 0x04]; // PK\x03\x04
    private static readonly byte[] MagicOle = [0xD0, 0xCF, 0x11, 0xE0]; // .doc/.xls legado (CFBF)
    private static readonly byte[] MagicPdf = [0x25, 0x50, 0x44, 0x46]; // %PDF
    private static readonly byte[] MagicElf = [0x7F, 0x45, 0x4C, 0x46]; // ELF
    private static readonly byte[] MagicGzip = [0x1F, 0x8B];             // gzip
    private static readonly byte[] MagicMz = [0x4D, 0x5A];               // PE/EXE (MZ)

    private readonly OpcoesUpload _opcoes;

    public ValidadorConteudoArquivo(IOptions<OpcoesUpload> opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        _opcoes = opcoes.Value;
    }

    public async Task<ResultadoValidacaoConteudo> ValidarAsync(
        Stream conteudo,
        string extensao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        var cabecalho = await LerCabecalhoAsync(conteudo, cancellationToken).ConfigureAwait(false);

        return (extensao ?? string.Empty).ToLowerInvariant() switch
        {
            "xlsx" => ValidarXlsx(conteudo, cabecalho),
            "csv" => ValidarTexto(cabecalho, ContentTypeCsv),
            "txt" => ValidarTexto(cabecalho, ContentTypeTxt),
            "xml" => await ValidarXmlAsync(conteudo, cabecalho, cancellationToken).ConfigureAwait(false),
            _ => ResultadoValidacaoConteudo.Invalido("extensao_nao_suportada"),
        };
    }

    private ResultadoValidacaoConteudo ValidarXlsx(Stream conteudo, byte[] cabecalho)
    {
        if (!ComecaCom(cabecalho, MagicZip))
        {
            return ResultadoValidacaoConteudo.Invalido("assinatura_incoerente");
        }

        try
        {
            conteudo.Position = 0;
            using var zip = new ZipArchive(conteudo, ZipArchiveMode.Read, leaveOpen: true);

            if (zip.Entries.Count == 0 || zip.Entries.Count > _opcoes.MaxEntradasZip)
            {
                return ResultadoValidacaoConteudo.Invalido("zip_entradas");
            }

            long totalDescompactado = 0;
            var temContentTypes = false;

            foreach (var entrada in zip.Entries)
            {
                if (EhZipSlip(entrada.FullName))
                {
                    return ResultadoValidacaoConteudo.Invalido("zip_slip");
                }

                if (string.Equals(entrada.FullName, EntradaContentTypes, StringComparison.Ordinal))
                {
                    temContentTypes = true;
                }

                totalDescompactado += entrada.Length;
                if (totalDescompactado > _opcoes.MaxTamanhoDescompactadoBytes)
                {
                    return ResultadoValidacaoConteudo.Invalido("zip_bomb_tamanho");
                }

                if (entrada.CompressedLength > 0)
                {
                    var razao = (double)entrada.Length / entrada.CompressedLength;
                    if (razao > _opcoes.RazaoMaximaCompressao)
                    {
                        return ResultadoValidacaoConteudo.Invalido("zip_bomb_razao");
                    }
                }
            }

            if (!temContentTypes)
            {
                // Um .xlsx válido (OpenXML) sempre contém [Content_Types].xml.
                return ResultadoValidacaoConteudo.Invalido("openxml_invalido");
            }

            return ResultadoValidacaoConteudo.Ok(ContentTypeXlsx);
        }
        catch (InvalidDataException)
        {
            // ZIP corrompido/malformado.
            return ResultadoValidacaoConteudo.Invalido("zip_invalido");
        }
    }

    private static ResultadoValidacaoConteudo ValidarTexto(byte[] cabecalho, string contentType)
    {
        // Rejeita contêineres/binários disfarçados de texto.
        if (ComecaCom(cabecalho, MagicZip) || ComecaCom(cabecalho, MagicOle) || ComecaCom(cabecalho, MagicPdf)
            || ComecaCom(cabecalho, MagicElf) || ComecaCom(cabecalho, MagicGzip) || ComecaCom(cabecalho, MagicMz))
        {
            return ResultadoValidacaoConteudo.Invalido("assinatura_binaria");
        }

        // Byte NUL no cabeçalho denuncia binário — texto real não contém NUL.
        foreach (var b in cabecalho)
        {
            if (b == 0x00)
            {
                return ResultadoValidacaoConteudo.Invalido("conteudo_binario");
            }
        }

        return ResultadoValidacaoConteudo.Ok(contentType);
    }

    private static async Task<ResultadoValidacaoConteudo> ValidarXmlAsync(
        Stream conteudo,
        byte[] cabecalho,
        CancellationToken cancellationToken)
    {
        // XML nunca é um contêiner ZIP/binário.
        if (ComecaCom(cabecalho, MagicZip) || ComecaCom(cabecalho, MagicOle) || ComecaCom(cabecalho, MagicGzip))
        {
            return ResultadoValidacaoConteudo.Invalido("assinatura_incoerente");
        }

        try
        {
            conteudo.Position = 0;

            // Settings ENDURECIDOS (XXE/expansão bloqueados — Prompt 07), sem fechar o stream.
            var settings = XmlSeguro.Configuracoes();
            settings.CloseInput = false;
            settings.Async = true;

            using var leitor = XmlReader.Create(conteudo, settings);

            var achouElemento = false;
            while (await leitor.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (leitor.NodeType == XmlNodeType.Element)
                {
                    achouElemento = true;
                }
            }

            return achouElemento
                ? ResultadoValidacaoConteudo.Ok(ContentTypeXml)
                : ResultadoValidacaoConteudo.Invalido("xml_sem_elemento");
        }
        catch (XmlException)
        {
            return ResultadoValidacaoConteudo.Invalido("xml_malformado");
        }
    }

    private static async Task<byte[]> LerCabecalhoAsync(Stream conteudo, CancellationToken cancellationToken)
    {
        conteudo.Position = 0;
        var buffer = new byte[16];
        var lidos = await conteudo.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        conteudo.Position = 0;
        return lidos == buffer.Length ? buffer : buffer[..lidos];
    }

    private static bool ComecaCom(byte[] dados, byte[] prefixo)
    {
        if (dados.Length < prefixo.Length)
        {
            return false;
        }

        for (var i = 0; i < prefixo.Length; i++)
        {
            if (dados[i] != prefixo[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool EhZipSlip(string? nomeEntrada)
    {
        if (string.IsNullOrWhiteSpace(nomeEntrada))
        {
            return true;
        }

        // Diretório de entrada (termina em '/') é aceitável; o traversal é o que se bloqueia.
        if (nomeEntrada.Contains("..", StringComparison.Ordinal)
            || nomeEntrada.StartsWith('/')
            || nomeEntrada.StartsWith('\\')
            || nomeEntrada.Contains(':', StringComparison.Ordinal)
            || Path.IsPathRooted(nomeEntrada))
        {
            return true;
        }

        return false;
    }
}
