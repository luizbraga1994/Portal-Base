using System.Globalization;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Incidentes;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// AÇÕES de GESTÃO DE INCIDENTES pela tela (registrar, analisar, conter, classificar risco ao titular,
// anotar, encerrar) + linha do tempo. Embrulham o caso de uso ServicoIncidentes — autorização de PLATAFORMA
// (seguranca.incidentes.gerir) e auditoria no servidor. Entradas do cliente (tipo, severidade, contribuinte,
// classificação) são HOSTIS: validadas aqui (servidor) contra listas fechadas; nada de Exception.Message de
// infraestrutura na tela (só a mensagem de regra do domínio).
// =====================================================================================================

/// <summary>Dados do formulário "Registrar incidente" (valores crus da tela — validados no servidor).</summary>
public sealed record NovoIncidenteVm(
    string? Tipo, string? Severidade, string? Titulo, string? Descricao, string? SistemasAfetados, string? CategoriasDados, Guid? TenantAfetadoId);

/// <summary>Entrada da linha do tempo do incidente, com o NOME do ator quando for usuário.</summary>
public sealed record EntradaTimelineVm(int Sequencia, string Tipo, string Ator, string Descricao, DateTimeOffset OcorridoEmUtc);

/// <summary>Linha do tempo + nome do responsável pela classificação (quando houver).</summary>
public sealed record AcompanhamentoIncidenteVm(IReadOnlyList<EntradaTimelineVm> Timeline, string? Responsavel);

/// <summary>Contribuinte que pode ser apontado como afetado (lista autorizada da plataforma).</summary>
public sealed record OpcaoContribuinteVm(Guid Id, string Nome);

/// <summary>Ação de andamento escolhida na tela do incidente.</summary>
public enum AcaoIncidenteUi
{
    IniciarAnalise = 1,
    RegistrarContencao = 2,
    AdicionarNota = 3,
    ClassificarRisco = 4,
    Encerrar = 5,
}

public interface IDadosAcoesIncidentes
{
    Task<IReadOnlyList<OpcaoContribuinteVm>> ListarContribuintesAsync(CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> RegistrarAsync(NovoIncidenteVm dados, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> ExecutarAsync(
        Guid id, AcaoIncidenteUi acao, string? texto, bool envolveDadosPessoais = false, string? classificacao = null, CancellationToken cancellationToken = default);

    Task<(AcompanhamentoIncidenteVm? Item, EstadoConsultaUi Estado)> ObterAcompanhamentoAsync(Guid id, string? responsavelId, CancellationToken cancellationToken = default);
}

public sealed class DadosAcoesIncidentes : IDadosAcoesIncidentes
{
    private readonly ServicoIncidentes _servico;
    private readonly ConsultaIncidentesAutorizada _consulta;
    private readonly CadastroContribuinte _contribuintes;
    private readonly IConsultaNomesAtores _nomes;
    private readonly IContextoSeguranca _seguranca;

    public DadosAcoesIncidentes(
        ServicoIncidentes servico, ConsultaIncidentesAutorizada consulta, CadastroContribuinte contribuintes, IConsultaNomesAtores nomes, IContextoSeguranca seguranca)
    {
        ArgumentNullException.ThrowIfNull(servico);
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(contribuintes);
        ArgumentNullException.ThrowIfNull(nomes);
        ArgumentNullException.ThrowIfNull(seguranca);
        _servico = servico;
        _consulta = consulta;
        _contribuintes = contribuintes;
        _nomes = nomes;
        _seguranca = seguranca;
    }

    /// <summary>Contribuintes da plataforma (para "contribuinte afetado"); vazio se o perfil não pode listá-los.</summary>
    public async Task<IReadOnlyList<OpcaoContribuinteVm>> ListarContribuintesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var lista = await _contribuintes.ListarAsync(cancellationToken).ConfigureAwait(false);
            return lista.Select(c => new OpcaoContribuinteVm(c.Id, c.Nome)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return [];
        }
    }

    public async Task<ResultadoAcaoVm> RegistrarAsync(NovoIncidenteVm dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);
        if (!TentarEnum<TipoIncidente>(dados.Tipo, out var tipo) || !TentarEnum<SeveridadeIncidente>(dados.Severidade, out var severidade))
        {
            return Invalido("Escolha o tipo e a severidade do incidente.");
        }

        if (string.IsNullOrWhiteSpace(dados.Titulo) || string.IsNullOrWhiteSpace(dados.Descricao))
        {
            return Invalido("Informe o título e a descrição do incidente.");
        }

        try
        {
            // O contribuinte afetado vem do cliente: só vale se estiver na lista AUTORIZADA da plataforma.
            if (dados.TenantAfetadoId is { } tenant)
            {
                var permitidos = await _contribuintes.ListarAsync(cancellationToken).ConfigureAwait(false);
                if (!permitidos.Any(c => c.Id == tenant))
                {
                    return Invalido("Contribuinte afetado inválido. Escolha um da lista.");
                }
            }

            var incidente = await _servico.RegistrarAsync(
                new NovoIncidente(dados.TenantAfetadoId, tipo, severidade, dados.Titulo, dados.Descricao,
                    Vazio(dados.SistemasAfetados), Vazio(dados.CategoriasDados)),
                cancellationToken).ConfigureAwait(false);
            return new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Incidente registrado",
                $"Incidente {incidente.Codigo} registrado como Detectado. Siga com a análise e a contenção.", incidente.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return MapearExcecao(ex);
        }
    }

    public async Task<ResultadoAcaoVm> ExecutarAsync(
        Guid id, AcaoIncidenteUi acao, string? texto, bool envolveDadosPessoais = false, string? classificacao = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(acao))
        {
            return Invalido("Ação inválida.");
        }

        try
        {
            switch (acao)
            {
                case AcaoIncidenteUi.IniciarAnalise:
                    await _servico.IniciarAnaliseAsync(id, Vazio(texto), cancellationToken).ConfigureAwait(false);
                    return Ok("Análise iniciada", "O incidente está Em análise.");
                case AcaoIncidenteUi.RegistrarContencao:
                    if (string.IsNullOrWhiteSpace(texto)) { return Invalido("Descreva a medida de contenção aplicada."); }
                    await _servico.RegistrarContencaoAsync(id, texto, cancellationToken).ConfigureAwait(false);
                    return Ok("Contenção registrada", "O incidente está Contido.");
                case AcaoIncidenteUi.AdicionarNota:
                    if (string.IsNullOrWhiteSpace(texto)) { return Invalido("Escreva a nota."); }
                    await _servico.AdicionarNotaAsync(id, texto, cancellationToken).ConfigureAwait(false);
                    return Ok("Nota registrada", "A nota entrou na linha do tempo.");
                case AcaoIncidenteUi.ClassificarRisco:
                    if (!TentarEnum<ClassificacaoRiscoTitular>(classificacao, out var risco) || risco == ClassificacaoRiscoTitular.NaoAvaliado)
                    {
                        return Invalido("Escolha a classificação do risco ao titular.");
                    }

                    if (string.IsNullOrWhiteSpace(texto)) { return Invalido("Justifique a classificação."); }

                    // Responsável = quem decide (principal da sessão, no servidor) — nunca um id vindo do cliente.
                    await _servico.ClassificarRiscoTitularAsync(id, envolveDadosPessoais, risco, texto, _seguranca.PrincipalId ?? string.Empty, cancellationToken)
                        .ConfigureAwait(false);
                    return Ok("Risco classificado", "A decisão, a justificativa e o responsável foram registrados.");
                default:
                    if (string.IsNullOrWhiteSpace(texto)) { return Invalido("Escreva o resumo do encerramento."); }
                    await _servico.EncerrarAsync(id, texto, cancellationToken).ConfigureAwait(false);
                    return Ok("Incidente encerrado", "O incidente foi encerrado; a linha do tempo fica imutável.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return MapearExcecao(ex);
        }
    }

    public async Task<(AcompanhamentoIncidenteVm? Item, EstadoConsultaUi Estado)> ObterAcompanhamentoAsync(
        Guid id, string? responsavelId, CancellationToken cancellationToken = default)
    {
        try
        {
            var timeline = await _consulta.ObterTimelineAsync(id, cancellationToken).ConfigureAwait(false);
            var ids = timeline.Select(t => t.AtorId).Append(responsavelId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var nomes = ids.Count == 0
                ? new Dictionary<string, IdentificacaoAtor>(StringComparer.Ordinal)
                : await _nomes.IdentificarAsync(ids, cancellationToken).ConfigureAwait(false);
            string Nome(string? ator) => ator is null ? "—" : nomes.TryGetValue(ator, out var n) ? n.Nome : ator;

            var itens = timeline
                .Select(t => new EntradaTimelineVm(t.Sequencia, RotuloEntrada(t.Tipo), Nome(t.AtorId), t.Descricao, t.OcorridoEmUtc))
                .ToList();
            return (new AcompanhamentoIncidenteVm(itens, responsavelId is null ? null : Nome(responsavelId)), EstadoConsultaUi.Ok);
        }
        catch (Exception ex) when (ex is NaoAutenticadoException or AutorizacaoNegadaException or MfaRequeridoException)
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return (null, EstadoConsultaUi.Erro);
        }
    }

    public static string RotuloEntrada(TipoEntradaTimeline tipo) => tipo switch
    {
        TipoEntradaTimeline.Deteccao => "Detecção",
        TipoEntradaTimeline.Analise => "Análise",
        TipoEntradaTimeline.Contencao => "Contenção",
        TipoEntradaTimeline.Classificacao => "Classificação",
        TipoEntradaTimeline.Evidencia => "Evidência",
        TipoEntradaTimeline.Nota => "Nota",
        TipoEntradaTimeline.Encerramento => "Encerramento",
        _ => tipo.ToString(),
    };

    /// <summary>Exceção → resultado seguro. Só a mensagem de REGRA do domínio é mostrada (sem detalhe interno).</summary>
    public static ResultadoAcaoVm MapearExcecao(Exception excecao)
    {
        ArgumentNullException.ThrowIfNull(excecao);
        return excecao switch
        {
            AutorizacaoNegadaException or NaoAutenticadoException => new ResultadoAcaoVm(false, EstadoAcaoUi.SemPermissao, "Sem permissão",
                "A gestão de incidentes exige a permissão de plataforma (seguranca.incidentes.gerir). A decisão é do servidor."),
            MfaRequeridoException => new ResultadoAcaoVm(false, EstadoAcaoUi.MfaRequerida, "Verificação em duas etapas exigida",
                "Esta operação exige o segundo fator (MFA). Confirme a verificação e tente novamente."),
            EstadoInvalidoException e => new ResultadoAcaoVm(false, EstadoAcaoUi.Conflito, "Ação não aplicável ao estado atual", e.Message),
            EntradaInvalidaException e => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Dados inválidos", e.Message),
            _ => new ResultadoAcaoVm(false, EstadoAcaoUi.Erro, "Não foi possível concluir",
                "A operação falhou. Atualize a página e tente novamente; nenhum detalhe interno é exibido."),
        };
    }

    private static bool TentarEnum<T>(string? valor, out T resultado) where T : struct, Enum
        => Enum.TryParse(valor, ignoreCase: false, out resultado) && Enum.IsDefined(resultado)
            && !int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static ResultadoAcaoVm Ok(string titulo, string mensagem) => new(true, EstadoAcaoUi.Ok, titulo, mensagem);

    private static ResultadoAcaoVm Invalido(string mensagem) => new(false, EstadoAcaoUi.Invalido, "Dados inválidos", mensagem);
}
