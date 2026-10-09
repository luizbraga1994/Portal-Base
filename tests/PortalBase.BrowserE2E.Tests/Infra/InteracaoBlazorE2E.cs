using System.Text;
using Microsoft.Playwright;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>
/// P62-R1 — interação com páginas <c>@rendermode InteractiveServer</c>. O HTML PRÉ-RENDERIZADO aparece antes de o
/// circuito Blazor conectar; ao conectar, a página re-renderiza (muitas vezes passando por um estado "carregando"
/// que REMOVE e recria os elementos). Um clique/digitação nesse intervalo não chega ao servidor ou se perde
/// (reproduzido: 2ª aba clicou "Reprocessar" antes do circuito; arquivo escolhido antes do re-render sumiu do
/// input). Em vez de sleep cego: (a) a ação idempotente é repetida até o EFEITO observável aparecer; (b) para
/// ações não idempotentes (envio de formulário), aguarda-se o 1º lote de render do circuito no WebSocket do Blazor.
/// Nada de bypass: cada repetição é uma interação real do usuário processada pelo servidor.
/// </summary>
internal static class InteracaoBlazorE2E
{
    private const int Tentativas = 20;
    private const int EsperaPorTentativaMs = 1500;
    private static readonly TimeSpan EsperaCircuito = TimeSpan.FromSeconds(30);

    /// <summary>Executa <paramref name="acao"/> até <paramref name="efeito"/> ficar visível (falha real ao esgotar).</summary>
    public static async Task RepetirAteAsync(Func<Task> acao, ILocator efeito)
    {
        ArgumentNullException.ThrowIfNull(acao);
        ArgumentNullException.ThrowIfNull(efeito);

        for (var tentativa = 0; tentativa < Tentativas; tentativa++)
        {
            try
            {
                await acao().ConfigureAwait(false);
                await efeito.First.WaitForAsync(new LocatorWaitForOptions { Timeout = EsperaPorTentativaMs }).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // Circuito ainda não interativo, página no estado "carregando" ou servidor processando — repete.
            }
        }

        // Última verificação: se ainda não apareceu, falha com o estado REAL da página (diagnóstico, sem segredo).
        try
        {
            await efeito.First.WaitForAsync(new LocatorWaitForOptions { Timeout = EsperaPorTentativaMs }).ConfigureAwait(false);
        }
        catch (TimeoutException excecao)
        {
            var pagina = efeito.Page;
            // Só a área de conteúdo (o menu lateral ocuparia todo o trecho do diagnóstico).
            var conteudo = pagina.Locator("main .content");
            var texto = await (await conteudo.CountAsync().ConfigureAwait(false) > 0 ? conteudo.First : pagina.Locator("body"))
                .InnerTextAsync().ConfigureAwait(false);
            texto = texto.Replace('\n', ' ');
            throw new Xunit.Sdk.XunitException(
                $"Efeito não observado após {Tentativas} tentativas. URL={pagina.Url} | tela: {(texto.Length > 600 ? texto[..600] : texto)}",
                excecao);
        }
    }

    /// <summary>Abre a confirmação de "Reprocessar" da linha da carta morta (aguarda o modal do servidor).</summary>
    public static Task AbrirConfirmacaoReprocessoAsync(IPage pagina, ILocator linha)
    {
        ArgumentNullException.ThrowIfNull(pagina);
        ArgumentNullException.ThrowIfNull(linha);
        return RepetirAteAsync(
            () => linha.Locator("button:has-text('Reprocessar')").ClickAsync(new LocatorClickOptions { Timeout = EsperaPorTentativaMs }),
            pagina.Locator(".modal-foot button.btn-primary"));
    }

    /// <summary>
    /// Executa uma ação que CARREGA uma página interativa (navegação/envio de formulário) e só retorna depois que o
    /// circuito Blazor enviou o primeiro lote de render (<c>JS.RenderBatch</c> no WebSocket <c>/_blazor</c>) E os
    /// lotes seguintes cessaram por <see cref="QuietudeRender"/> (ex.: "carregando" → conteúdo). A página fica estável.
    /// </summary>
    public static async Task ExecutarEAguardarCircuitoAsync(IPage pagina, Func<Task> acaoQueCarrega)
    {
        ArgumentNullException.ThrowIfNull(pagina);
        ArgumentNullException.ThrowIfNull(acaoQueCarrega);

        var renderizou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ultimoLote = DateTime.UtcNow;
        var trava = new object();

        void AoAbrirWebSocket(object? remetente, IWebSocket ws)
        {
            if (!ws.Url.Contains("/_blazor", StringComparison.Ordinal))
            {
                return;
            }

            ws.FrameReceived += (_, quadro) =>
            {
                var conteudo = quadro.Binary is { } binario ? Encoding.UTF8.GetString(binario) : quadro.Text ?? string.Empty;
                if (conteudo.Contains("JS.RenderBatch", StringComparison.Ordinal))
                {
                    lock (trava)
                    {
                        ultimoLote = DateTime.UtcNow;
                    }

                    renderizou.TrySetResult();
                }
            };
        }

        pagina.WebSocket += AoAbrirWebSocket;
        try
        {
            await acaoQueCarrega().ConfigureAwait(false);
            await renderizou.Task.WaitAsync(EsperaCircuito).ConfigureAwait(false);

            // Quietude: nenhum lote novo por QuietudeRender (limite total EsperaCircuito).
            var limite = DateTime.UtcNow + EsperaCircuito;
            while (DateTime.UtcNow < limite)
            {
                DateTime ultimo;
                lock (trava)
                {
                    ultimo = ultimoLote;
                }

                var restante = QuietudeRender - (DateTime.UtcNow - ultimo);
                if (restante <= TimeSpan.Zero)
                {
                    return;
                }

                await Task.Delay(restante).ConfigureAwait(false); // espera o fim da janela de quietude (não é sincronização cega)
            }
        }
        finally
        {
            pagina.WebSocket -= AoAbrirWebSocket;
        }
    }

    private static readonly TimeSpan QuietudeRender = TimeSpan.FromMilliseconds(750);
}
