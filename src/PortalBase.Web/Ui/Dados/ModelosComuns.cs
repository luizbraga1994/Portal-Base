namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// View-models COMUNS das telas. Projeções de UI (strings) — NUNCA tipos de domínio nem entidades EF na tela.
// Sem dados fictícios: quando o backend não fornece, o estado é SemTenant/SemPermissao/Erro (não zero, não mock).
// =====================================================================================================

/// <summary>Estado de uma consulta de UI (compartilhado pelas telas ligadas a read-models reais).</summary>
public enum EstadoConsultaUi
{
    Ok,
    Demonstracao,
    SemTenant,
    SemPermissao,
    Erro,
}

/// <summary>Filtro base de paginação da UI (Pagina/TamanhoPagina; limites do backend: 1..200, padrão 50).</summary>
public abstract record FiltroPaginadoVm
{
    public int Pagina { get; init; } = 1;

    public int TamanhoPagina { get; init; } = 50;
}

/// <summary>Envelope paginado genérico + estado, para as listas.</summary>
public sealed record ResultadoListaVm<T>(
    IReadOnlyList<T> Itens,
    int Pagina,
    int TamanhoPagina,
    long Total,
    EstadoConsultaUi Estado,
    string? Mensagem = null)
{
    public int TotalPaginas => TamanhoPagina < 1 ? 1 : (int)Math.Max(1, Math.Ceiling(Total / (double)TamanhoPagina));

    public bool TemAnterior => Pagina > 1;

    public bool TemProxima => Pagina < TotalPaginas;
}

/// <summary>Fábricas não-genéricas para <see cref="ResultadoListaVm{T}"/> (evita CA1000).</summary>
public static class ResultadoListaVm
{
    public static ResultadoListaVm<T> Vazio<T>(int pagina, int tamanho, EstadoConsultaUi estado, string? mensagem = null) =>
        new([], pagina < 1 ? 1 : pagina, tamanho, 0, estado, mensagem);
}

// ---- Arquivos de importação -------------------------------------------------------------------------

public sealed record FiltroArquivosVm : FiltroPaginadoVm
{
    /// <summary>Período inclusivo sobre EnviadoEmUtc (Prompt 81) — validado de novo no servidor.</summary>
    public DateTimeOffset? Desde { get; init; }
    public DateTimeOffset? Ate { get; init; }
    public string? Estado { get; init; }
    public string? ResultadoScan { get; init; }
    public string? Extensao { get; init; }
    public string? Sha256 { get; init; }
}

public sealed record RegistroArquivoVm(
    Guid Id, string Nome, string Extensao, string ContentType, long TamanhoBytes, string TamanhoLegivel,
    string HashCurto, string Estado, string EstadoBadge, string Scan, string ScanBadge,
    DateTimeOffset? EscaneadoEmUtc, string? MotivoRejeicao, DateTimeOffset EnviadoEmUtc, DateTimeOffset AtualizadoEmUtc);

// ---- Resultado de ação (command) --------------------------------------------------------------------

/// <summary>Estado do resultado de um COMMAND acionado pela UI (fail-closed, sem vazar detalhe interno).</summary>
public enum EstadoAcaoUi
{
    Ok = 0,
    SemTenant = 1,
    SemPermissao = 2,
    MfaRequerida = 3,
    Invalido = 4,
    Erro = 5,

    /// <summary>
    /// Bloqueado por dependência EXTERNA honesta (ex.: credencial de uma API externa ainda ausente).
    /// Não é falha do usuário nem erro interno — a operação não pode ocorrer no ambiente atual e NADA muda (P65).
    /// </summary>
    Bloqueado = 6,

    /// <summary>
    /// Conflito de ESTADO do processo (ex.: registro já processado, estado não elegível).
    /// A ação não se aplica ao estado atual — o usuário deve atualizar/rever, sem que nada seja alterado (P65).
    /// </summary>
    Conflito = 7,
}

/// <summary>Resultado seguro de uma ação de escrita — nunca traz stack/SQL/segredo; só sucesso + mensagem de UX.</summary>
/// <param name="Id">Identificador do registro criado/alterado, quando a ação devolve um (ex.: o usuário cadastrado).</param>
public sealed record ResultadoAcaoVm(bool Sucesso, EstadoAcaoUi Estado, string Titulo, string Mensagem, Guid? Id = null);
