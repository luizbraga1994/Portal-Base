using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using PortalBase.Web.Ui.Autorizacao;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// P62-R1 — hidrata o ESCOPO DO CIRCUITO interativo com o mesmo contexto que o <c>MainLayout</c> (render
/// estático) hidrata no escopo da requisição HTTP: principal (<see cref="PortadorPrincipalCircuito"/>),
/// contribuinte selecionado (<see cref="SelecaoTenant"/>) e projeção de permissões (<see cref="IAutorizacaoVisual"/>).
/// As páginas <c>@rendermode InteractiveServer</c> rodam, após o circuito conectar, num escopo de DI PRÓPRIO;
/// sem isto esse escopo ficava sem principal/tenant/permissões e toda tela autorizada caía em "Sem permissão".
/// A fonte é o <see cref="AuthenticationStateProvider"/> do circuito (o mesmo cookie já validado, revalidado
/// periodicamente pelo <see cref="RevalidadorSecurityStamp"/>) — nunca estado enviado pelo cliente. Fail-closed:
/// sem autenticação, com falha ou cancelamento, nada é hidratado; sem contribuinte selecionado, só as permissões de
/// plataforma; a decisão de cada ação segue no servidor (os serviços reautorizam cada command).
///
/// Ordenação SEM bloquear thread: no Blazor Web os componentes raiz chegam numa mensagem SEPARADA
/// (<c>UpdateRootComponents</c>), enfileirada no MESMO Dispatcher do circuito e não coberta pelos inbound activity
/// handlers. A hidratação é iniciada na parte síncrona de <see cref="OnCircuitOpenedAsync"/> (que roda no Dispatcher
/// ANTES dessa mensagem) e exposta em <see cref="Pronto"/>; toda página (<c>Components.PaginaPortal</c>, aplicada
/// por <c>Pages/_Imports.razor</c>) aguarda <see cref="Pronto"/> antes do seu ciclo de vida. Assim a página nunca
/// inicializa antes da hidratação ("Sem permissão") nem disputa o DbContext do escopo ("Connection must be valid
/// and open" — reproduzido no E2E integrado), e nenhuma thread do pool fica presa esperando I/O.
/// </summary>
public sealed partial class HidratacaoCircuito : CircuitHandler
{
    private readonly AuthenticationStateProvider _autenticacao;
    private readonly PortadorPrincipalCircuito _portador;
    private readonly SelecaoTenant _selecao;
    private readonly IAutorizacaoVisual _visual;
    private readonly ILogger<HidratacaoCircuito> _logger;
    private Task _pronto = Task.CompletedTask;

    public HidratacaoCircuito(
        AuthenticationStateProvider autenticacao,
        PortadorPrincipalCircuito portador,
        SelecaoTenant selecao,
        IAutorizacaoVisual visual,
        ILogger<HidratacaoCircuito> logger)
    {
        ArgumentNullException.ThrowIfNull(autenticacao);
        ArgumentNullException.ThrowIfNull(portador);
        ArgumentNullException.ThrowIfNull(selecao);
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(logger);
        _autenticacao = autenticacao;
        _portador = portador;
        _selecao = selecao;
        _visual = visual;
        _logger = logger;
    }

    /// <summary>
    /// Conclui quando a hidratação do escopo terminou (com sucesso, falha ou cancelamento — nunca lança). No render
    /// estático (sem circuito) já está concluída.
    /// </summary>
    public Task Pronto => _pronto;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Atribuído na parte SÍNCRONA: o item do Dispatcher que abre o circuito é processado antes do
        // UpdateRootComponents, então nenhuma página lê Pronto antes desta atribuição.
        _pronto = HidratarComSegurancaAsync(cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Segura a liberação do escopo enquanto a hidratação estiver em voo (não descarta DbContext no meio da consulta).</summary>
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) => _pronto;

    private async Task HidratarComSegurancaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await HidratarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Circuito encerrado durante a abertura (aba fechada/navegação): nada a hidratar — fail-closed.
        }
        catch (Exception excecao) when (excecao is not OutOfMemoryException)
        {
            // Fail-closed: o escopo permanece sem principal/tenant/permissões; nunca derruba o circuito nem vaza detalhe.
            LogFalhaHidratacao(_logger, excecao.GetType().Name);
        }
    }

    private async Task HidratarAsync(CancellationToken cancellationToken)
    {
        var estado = await _autenticacao.GetAuthenticationStateAsync().ConfigureAwait(false);
        var usuario = estado.User;
        if (usuario.Identity?.IsAuthenticated != true)
        {
            return; // sem sessão: nada a hidratar (as telas já são fail-closed)
        }

        _portador.Definir(usuario);

        var tenantClaim = usuario.FindFirst(ClaimsPortalBase.TenantSelecionado)?.Value;
        if (!Guid.TryParse(tenantClaim, out var tenantId))
        {
            // Sem contribuinte selecionado: só as permissões de PLATAFORMA (o provedor não projeta nada de contribuinte
            // sem tenant) — é o que as telas de plataforma abertas sem contribuinte (MainLayout) precisam.
            cancellationToken.ThrowIfCancellationRequested();
            await _visual.HidratarAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // Re-valida o vínculo no servidor (fail-closed) e projeta as permissões efetivas do usuário no tenant.
        // O token do circuito é verificado ANTES de iniciar o I/O, mas NÃO é repassado ao driver: o cancelamento de
        // um comando em voo no MySql.Data (KILL QUERY por conexão lateral) pode deixar leitor/conexão em estado
        // inválido — observado no E2E integrado (TaskCanceledException dentro do driver durante a hidratação, seguido
        // de erros "Connection must be valid and open"). São duas leituras curtas, limitadas pelo CommandTimeout.
        cancellationToken.ThrowIfCancellationRequested();
        await _selecao.HidratarAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
        await _visual.HidratarAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 6201, Level = LogLevel.Warning,
        Message = "Hidratação do circuito interativo falhou ({TipoExcecao}); escopo mantido sem contexto (fail-closed).")]
    private static partial void LogFalhaHidratacao(ILogger logger, string tipoExcecao);
}
