using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PortalBase.Application.Marca;
using PortalBase.Application.Multitenancy;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Marca;
using Xunit.Abstractions;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Onda 5 (gargalos — auditoria de 08/10/2026), MEDIDO no organismo integrado (Browser → Web/Kestrel → MySQL real):
/// em navegações COMPLETAS para Dados → Arquivos, com uma logo no contribuinte,
/// <list type="bullet">
/// <item>UI-001: a lista da página é consultada UMA vez (antes, o prerender consultava e o circuito consultava de novo);</item>
/// <item>UI-005: o layout NÃO lê a imagem da logo do banco (antes, a cada navegação, só para saber se havia logo). O
/// servidor só lê a imagem para SERVIR um download de verdade do navegador (resposta 200). Neste ambiente a sessão é
/// conferida a cada requisição (<c>Seguranca:Sessao:RevalidacaoSegundos=0</c>), o cookie é renovado sempre e o
/// ASP.NET Core marca a resposta <c>no-store</c> — então o navegador baixa a logo a cada navegação; o pedido condicional
/// (304 sem ler a imagem) é provado em <c>MarcaNaoModificadaTests</c>/<c>MarcaResumoMySqlTests</c>.</item>
/// </list>
/// Os números vão para <c>PORTALBASE_MEDIR_SAIDA</c> (quando definido) antes das verificações. Imagem fictícia.
/// Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class CargasPorNavegacaoE2ETests
{
    private const int Navegacoes = 3;
    private static readonly LocatorWaitForOptions Espera = new() { Timeout = 90_000 };

    private readonly AmbienteE2EIntegrado _ambiente;
    private readonly ITestOutputHelper _saida;

    public CargasPorNavegacaoE2ETests(AmbienteE2EIntegrado ambiente, ITestOutputHelper saida)
    {
        _ambiente = ambiente;
        _saida = saida;
    }

    [SkippableFact]
    public async Task Navegacao_Completa_Consulta_A_Pagina_Uma_Vez_E_So_Le_A_Imagem_Da_Logo_Para_Servir_Download()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var bancoTenant = await PrepararLogoAsync(cenario.A.UsuarioId, cenario.A.TenantId, 2 * 1024 * 1024);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(_ambiente.Navegador!, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        var downloadsLogo = 0;
        pagina.Response += (_, resposta) =>
        {
            if (resposta.Url.Contains("/branding/logo", StringComparison.Ordinal) && resposta.Status == 200)
            {
                Interlocked.Increment(ref downloadsLogo);
            }
        };

        var medicao = await MedirAsync(pagina, bancoTenant, () => Interlocked.Exchange(ref downloadsLogo, 0));

        var texto = "### UI-001/UI-005 — navegação completa (E2E integrado)\n\n"
            + "| Dados → Importações (F5) | consultas da lista | leituras da imagem da logo | downloads da logo pelo navegador | comandos SQL no banco do contribuinte |\n"
            + "|---|---|---|---|---|\n"
            + medicao.Linha("logo de 2 MB") + "\n\n";
        _saida.WriteLine(texto);
        if (Environment.GetEnvironmentVariable("PORTALBASE_MEDIR_SAIDA") is { Length: > 0 } arquivo)
        {
            await File.AppendAllTextAsync(arquivo, texto);
        }

        medicao.Listas.Should().OnlyContain(n => n > 0 && n == medicao.Listas[0], "a página carrega a lista (mesmo número em toda navegação)");
        medicao.Listas[0].Should().BeLessThanOrEqualTo(2, "UI-001: uma carga só (contagem + página), sem o prerender repetir");
        medicao.Imagens.Should().Equal(medicao.Downloads, "UI-005: a imagem só é lida para servir um download do navegador — nunca pelo layout");
    }

    private static async Task<Medicao> MedirAsync(IPage pagina, string bancoTenant, Func<int> zerarDownloads)
    {
        await IrAsync(pagina, "/dados/arquivos"); // aquece (cache de metadados da logo, compilação de consultas)

        using var contador = new ContadorComandosSqlE2E(bancoTenant);
        var medicao = new Medicao([], [], [], []);
        for (var i = 0; i < Navegacoes; i++)
        {
            contador.Limpar();
            zerarDownloads();
            await IrAsync(pagina, "/dados/arquivos");
            await pagina.Locator("main:has-text('Nenhum arquivo encontrado'), main table tbody tr").First.WaitForAsync(Espera);
            await Task.Delay(1_500); // o que mais a navegação fizer no banco
            var textos = contador.Textos;
            medicao.Listas.Add(textos.Count(t => t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && t.Contains("FROM `arquivo_importacao`", StringComparison.OrdinalIgnoreCase)));
            medicao.Imagens.Add(textos.Count(t => t.Contains("`Conteudo`", StringComparison.OrdinalIgnoreCase)));
            medicao.Downloads.Add(zerarDownloads());
            medicao.Total.Add(textos.Count);
        }

        return medicao;
    }

    /// <summary>Logo fictícia (PNG) do tamanho pedido no contribuinte A; devolve o nome do banco dele.</summary>
    private async Task<string> PrepararLogoAsync(Guid usuarioId, Guid tenantId, int tamanho)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        var contexto = await escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>().ResolverAsync(usuarioId.ToString(), tenantId);
        var imagem = new byte[tamanho];
        byte[] cabecalhoPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        cabecalhoPng.CopyTo(imagem, 0);
        Random.Shared.NextBytes(imagem.AsSpan(cabecalhoPng.Length));
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioMarcaTenant>();
        var marca = await repositorio.ObterAsync();
        if (marca is null)
        {
            marca = MarcaVisual.Criar(imagem, "image/png", DateTimeOffset.UtcNow, usuarioId);
        }
        else
        {
            marca.Atualizar(imagem, "image/png", DateTimeOffset.UtcNow, usuarioId);
        }

        await repositorio.SalvarAsync(marca);
        return contexto.NomeBaseFisica ?? throw new InvalidOperationException("Contribuinte sem banco.");
    }

    private static Task IrAsync(IPage pagina, string rota)
        => InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync(rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load }));

    private sealed record Medicao(List<int> Listas, List<int> Imagens, List<int> Downloads, List<int> Total)
    {
        public string Linha(string logo) => string.Create(CultureInfo.InvariantCulture,
            $"| {logo} | {string.Join("/", Listas)} | {string.Join("/", Imagens)} | {string.Join("/", Downloads)} | {string.Join("/", Total)} |");
    }
}
