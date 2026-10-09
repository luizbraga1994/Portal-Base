using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// ENVIO DE ARQUIVO POR PLANILHA (Prompt 74, §4) — superfície de UI do upload seguro. NÃO é um pipeline
// novo: embrulha o MESMO caso de uso de Aplicação (IngerirArquivo, P08), que permanece a AUTORIDADE de
// tenant/permissão/extensão/tamanho/conteúdo/antivírus/idempotência. O tenant é o resolvido no servidor
// (ITenantContext do circuito, via SelecaoTenant) — NUNCA um identificador vindo do cliente (regra 4).
// A UI apenas melhora a experiência (accept/tamanho/duplo-submit); o backend revalida tudo (§4).
// =====================================================================================================

/// <summary>Desfecho de um envio de arquivo mapeado para a UI (fail-closed; sem stack/segredo — regra 16).</summary>
public enum EstadoEnvioUi
{
    /// <summary>Aceito — arquivo recebido, validado e armazenado (assíncrono a jusante).</summary>
    Ok = 0,

    /// <summary>Idempotente — o mesmo conteúdo já existe no tenant; nada foi duplicado (§7/regra 12).</summary>
    Duplicado = 1,

    /// <summary>Rejeitado na validação (extensão/tamanho/conteúdo/antivírus) — motivo GENÉRICO.</summary>
    Rejeitado = 2,

    /// <summary>Sem o contribuinte selecionado (fail-closed).</summary>
    SemTenant = 3,

    /// <summary>Sem a permissão dados.importar (decisão do servidor).</summary>
    SemPermissao = 4,

    /// <summary>Requer segundo fator (MFA) para concluir.</summary>
    MfaRequerida = 5,

    /// <summary>Falha inesperada — nenhum detalhe interno é exibido.</summary>
    Erro = 6,
}

/// <summary>Resultado seguro do envio de arquivo — sucesso + estado + Id (quando aceito) + mensagem de UX.</summary>
public sealed record ResultadoEnvioVm(EstadoEnvioUi Estado, Guid? ArquivoId, string Titulo, string Mensagem);

/// <summary>
/// Restrições DECLARADAS pelo backend, expostas à UI apenas como pista de UX (accept/tamanho). NÃO são a
/// fronteira de segurança: extensão é derivada do nome no servidor e conferida contra o CONTEÚDO real,
/// e o tamanho é aplicado incrementalmente no streaming server-side (§4). O browser não é confiável.
/// </summary>
public sealed record RestricoesEnvioVm(long MaxTamanhoBytes, IReadOnlyList<string> ExtensoesPermitidas)
{
    /// <summary>Atributo <c>accept</c> do <c>&lt;InputFile&gt;</c> (só filtro de UX no seletor — não valida).</summary>
    public string Accept => string.Join(",", ExtensoesPermitidas.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => "." + e.Trim().TrimStart('.')));

    /// <summary>Tamanho máximo em MB, arredondado, para exibição.</summary>
    public int MaxTamanhoMb => (int)Math.Max(1, MaxTamanhoBytes / (1024 * 1024));

    /// <summary>Lista legível das extensões aceitas (ex.: "xlsx, csv, xml, txt").</summary>
    public string ExtensoesRotulo => string.Join(", ", ExtensoesPermitidas);
}

/// <summary>
/// Mapeamento PURO do desfecho/exceção do caso de uso <see cref="IngerirArquivo"/> para um resultado de UI
/// seguro (<see cref="ResultadoEnvioVm"/>) — sem I/O, testável isoladamente, NUNCA repassa detalhe interno
/// (regra 16). Espelha o padrão dos demais mapeadores de ação (MapeadorAcaoProcesso/MapeadorAcaoAdmin).
/// </summary>
public static class MapeadorEnvio
{
    /// <summary>Sem contribuinte selecionado — fail-closed antes mesmo do caso de uso.</summary>
    public static ResultadoEnvioVm SemTenant() => new(EstadoEnvioUi.SemTenant, null, "Selecione um contribuinte",
        "O envio ocorre no contexto de um contribuinte autorizado. Selecione um contribuinte e tente novamente.");

    /// <summary>Traduz o desfecho do caso de uso para a UI (aceito/duplicado/rejeitado).</summary>
    public static ResultadoEnvioVm Mapear(ResultadoIngestao resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        return resultado.Situacao switch
        {
            SituacaoIngestao.Aceito => new ResultadoEnvioVm(EstadoEnvioUi.Ok, resultado.ArquivoId, "Arquivo recebido",
                "Arquivo aceito, validado e armazenado com segurança. A normalização/ingestão prossegue no servidor — acompanhe em Arquivos e Importações."),
            SituacaoIngestao.Duplicado => new ResultadoEnvioVm(EstadoEnvioUi.Duplicado, resultado.ArquivoId, "Arquivo já enviado",
                "Este mesmo conteúdo já havia sido recebido para o contribuinte atual. Nada foi duplicado (operação idempotente)."),
            _ => new ResultadoEnvioVm(EstadoEnvioUi.Rejeitado, null, "Arquivo rejeitado",
                "O arquivo foi rejeitado na validação de segurança (tipo/tamanho/conteúdo). Verifique o formato e o tamanho e tente novamente."),
        };
    }

    /// <summary>Traduz EXCEÇÕES tipadas (tenant/autorização/MFA/autenticação/erro) para a UI — nunca vaza stack/segredo.</summary>
    public static ResultadoEnvioVm MapearExcecao(Exception excecao)
    {
        ArgumentNullException.ThrowIfNull(excecao);
        return excecao switch
        {
            TenantContextAusenteException => SemTenant(),
            AutorizacaoNegadaException => new ResultadoEnvioVm(EstadoEnvioUi.SemPermissao, null, "Sem permissão",
                "Seu perfil não pode importar dados (dados.importar). A decisão é do servidor."),
            MfaRequeridoException => new ResultadoEnvioVm(EstadoEnvioUi.MfaRequerida, null, "Verificação em duas etapas exigida",
                "Esta operação exige o segundo fator (MFA). Confirme a verificação em duas etapas e tente novamente."),
            NaoAutenticadoException => new ResultadoEnvioVm(EstadoEnvioUi.SemPermissao, null, "Sessão inválida",
                "Autentique-se novamente para concluir o envio."),
            _ => new ResultadoEnvioVm(EstadoEnvioUi.Erro, null, "Não foi possível concluir",
                "O envio falhou. Nenhum detalhe interno é exibido. Tente novamente em instantes."),
        };
    }
}

/// <summary>Fonte de AÇÃO de envio de arquivo por planilha (embrulha o caso de uso IngerirArquivo — P08).</summary>
public interface IDadosEnvioArquivo
{
    /// <summary>Restrições de UX declaradas pelo backend (o backend revalida sempre).</summary>
    RestricoesEnvioVm Restricoes { get; }

    /// <summary>Envia o conteúdo (stream sequencial) ao caso de uso seguro; mapeia o desfecho para a UI.</summary>
    Task<ResultadoEnvioVm> EnviarAsync(string? nomeOriginal, Stream conteudo, CancellationToken cancellationToken = default);
}

/// <summary>Implementação: fail-closed por tenant + mapeamento de desfecho/exceção → estado de UI seguro (§4/§22).</summary>
public sealed class DadosEnvioArquivo : DadosTenantBase, IDadosEnvioArquivo
{
    private readonly IngerirArquivo _ingerir;
    private readonly OpcoesUpload _opcoes;

    public DadosEnvioArquivo(IngerirArquivo ingerir, OpcoesUpload opcoes, SelecaoTenant selecao) : base(selecao)
    {
        ArgumentNullException.ThrowIfNull(ingerir);
        ArgumentNullException.ThrowIfNull(opcoes);
        _ingerir = ingerir;
        _opcoes = opcoes;
    }

    public RestricoesEnvioVm Restricoes => new(_opcoes.MaxTamanhoBytes, _opcoes.ExtensoesPermitidas);

    public async Task<ResultadoEnvioVm> EnviarAsync(string? nomeOriginal, Stream conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        // Fail-closed na UI: sem contribuinte selecionado, nem chega ao caso de uso (o backend também recusaria).
        if (SemTenant)
        {
            return MapeadorEnvio.SemTenant();
        }

        try
        {
            var resultado = await _ingerir.ExecutarAsync(new ComandoIngestao(nomeOriginal, conteudo), cancellationToken).ConfigureAwait(false);
            return MapeadorEnvio.Mapear(resultado);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            // Fail-closed e sem vazar detalhe interno (regra 16 / §22).
            return MapeadorEnvio.MapearExcecao(excecao);
        }
    }
}
