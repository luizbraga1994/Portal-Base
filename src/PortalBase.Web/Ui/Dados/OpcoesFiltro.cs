using PortalBase.Domain.Arquivos;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Web.Ui.Dados;

/// <summary>Uma opção de filtro: <c>Valor</c> (nome do enum enviado ao facade, que faz <c>Enum.Parse</c>) + <c>Rotulo</c> pt-BR.</summary>
public sealed record OpcaoFiltro(string Valor, string Rotulo);

/// <summary>
/// Opções de filtro DERIVADAS dos enums de domínio (Prompt 70/74) — fonte única. As telas NÃO
/// reliteralizam as listas (o que causa DRIFT quando o enum muda — um filtro que envia um valor que o enum
/// não tem). Cada <c>Valor</c> usa
/// <c>nameof(Enum.Membro)</c>: se o membro for renomeado/removido, o build QUEBRA (proteção de compilação
/// contra drift). O rótulo pt-BR é curado uma única vez aqui; a ordem é a da UI. O nome do enum é o valor
/// enviado ao facade (que já faz <c>Enum.Parse</c>). O sentinela "Todos/Todas" (→ null) permanece na view
/// (conceito de UI). Puro, sem I/O.
/// </summary>
public static class OpcoesFiltro
{
    // ---- Arquivos ----
    public static IReadOnlyList<OpcaoFiltro> EstadosArquivo { get; } =
    [
        new(nameof(EstadoArquivo.Recebido), "Recebido"),
        new(nameof(EstadoArquivo.Armazenado), "Armazenado"),
        new(nameof(EstadoArquivo.Processando), "Processando"),
        new(nameof(EstadoArquivo.Processado), "Processado"),
        new(nameof(EstadoArquivo.Rejeitado), "Rejeitado"),
        new(nameof(EstadoArquivo.Falhado), "Falhado"),
        new(nameof(EstadoArquivo.Expirado), "Expirado"),
    ];

    public static IReadOnlyList<OpcaoFiltro> ResultadosScan { get; } =
    [
        new(nameof(ResultadoScan.NaoEscaneado), "Não escaneado"),
        new(nameof(ResultadoScan.Limpo), "Limpo"),
        new(nameof(ResultadoScan.Infectado), "Infectado"),
        new(nameof(ResultadoScan.Erro), "Erro"),
        new(nameof(ResultadoScan.Indisponivel), "Indisponível"),
    ];

    // ---- Incidentes (plataforma) ----
    public static IReadOnlyList<OpcaoFiltro> SeveridadesIncidente { get; } =
    [
        new(nameof(SeveridadeIncidente.Critico), "Crítico"),
        new(nameof(SeveridadeIncidente.Alto), "Alto"),
        new(nameof(SeveridadeIncidente.Medio), "Médio"),
        new(nameof(SeveridadeIncidente.Baixo), "Baixo"),
        new(nameof(SeveridadeIncidente.Informativo), "Informativo"),
    ];

    public static IReadOnlyList<OpcaoFiltro> StatusIncidente { get; } =
    [
        new(nameof(Domain.Incidentes.StatusIncidente.Detectado), "Detectado"),
        new(nameof(Domain.Incidentes.StatusIncidente.EmAnalise), "Em análise"),
        new(nameof(Domain.Incidentes.StatusIncidente.Contido), "Contido"),
        new(nameof(Domain.Incidentes.StatusIncidente.Encerrado), "Encerrado"),
    ];

    public static IReadOnlyList<OpcaoFiltro> TiposIncidente { get; } =
    [
        new(nameof(TipoIncidente.Tecnico), "Técnico"),
        new(nameof(TipoIncidente.ComDadosPessoais), "Com dados pessoais"),
    ];
}
