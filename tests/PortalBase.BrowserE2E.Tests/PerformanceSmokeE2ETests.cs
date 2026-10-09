using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;
using Xunit.Abstractions;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 56 — SMOKE de PERFORMANCE (Fase 2/4/25): mede a LATÊNCIA REAL do caminho de render Web
/// (Kestrel + Blazor SSR) neste ambiente, <b>sem MySQL</b> — apenas páginas fail-closed (login/home) e
/// o endpoint de liveness. Não é load/stress/soak (BLOCKED sem infra), não afirma capacidade de
/// usuários. Registra P50/P90/P95/P99/Máx num artefato (gitignored) para o baseline — números MEDIDOS,
/// nunca inventados. Não impõe SLA arbitrário (só verifica sucesso das navegações).
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class PerformanceSmokeE2ETests
{
    private const int Amostras = 25;
    private const int Aquecimento = 3;

    private static readonly JsonSerializerOptions OpcoesJson = new() { WriteIndented = true };

    private readonly AmbienteE2E _ambiente;
    private readonly ITestOutputHelper _saida;

    public PerformanceSmokeE2ETests(AmbienteE2E ambiente, ITestOutputHelper saida)
    {
        _ambiente = ambiente;
        _saida = saida;
    }

    [SkippableFact]
    public async Task Baseline_Latencia_Render_Web_Sem_Banco()
    {
        Skip.IfNot(_ambiente.NavegadorDisponivel,
            "BLOCKED: navegador indisponível — " + (_ambiente.MotivoBloqueioNavegador ?? "motivo desconhecido"));

        var rotas = new[] { "/conta/entrar", "/", "/health/live" };
        var medidas = new List<MedidaRota>();

        await using var contexto = await _ambiente.Navegador!.NewContextAsync(new BrowserNewContextOptions { Locale = "pt-BR" });
        var pagina = await contexto.NewPageAsync();

        foreach (var rota in rotas)
        {
            var url = _ambiente.BaseUrl + rota;

            for (var i = 0; i < Aquecimento; i++)
            {
                await pagina.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            }

            var amostrasMs = new List<double>(Amostras);
            for (var i = 0; i < Amostras; i++)
            {
                var relogio = Stopwatch.StartNew();
                var resposta = await pagina.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                relogio.Stop();

                resposta.Should().NotBeNull();
                resposta!.Status.Should().BeLessThan(400, $"a navegação a {rota} deve ter sucesso");
                amostrasMs.Add(relogio.Elapsed.TotalMilliseconds);
            }

            medidas.Add(MedidaRota.De(rota, amostrasMs));
        }

        // Registra o baseline MEDIDO (artefato gitignored) para transcrição no PERFORMANCE-BASELINE.md.
        var caminho = Path.Combine(_ambiente.DiretorioEvidencias, "perf-baseline.json");
        var json = JsonSerializer.Serialize(
            new { medidoEmUtc = DateTime.UtcNow.ToString("O"), amostrasPorRota = Amostras, navegador = _ambiente.InfoNavegador, rotas = medidas },
            OpcoesJson);
        await File.WriteAllTextAsync(caminho, json);

        foreach (var m in medidas)
        {
            _saida.WriteLine(m.ToString());
        }

        medidas.Should().HaveCount(rotas.Length);
        medidas.Should().OnlyContain(m => m.Amostras == Amostras);
    }

    private sealed record MedidaRota(string Rota, int Amostras, double MinMs, double P50Ms, double P90Ms, double P95Ms, double P99Ms, double MaxMs, double MediaMs)
    {
        public static MedidaRota De(string rota, List<double> amostras)
        {
            var ordenado = amostras.OrderBy(x => x).ToList();
            return new MedidaRota(
                rota,
                ordenado.Count,
                Arredonda(ordenado[0]),
                Percentil(ordenado, 0.50),
                Percentil(ordenado, 0.90),
                Percentil(ordenado, 0.95),
                Percentil(ordenado, 0.99),
                Arredonda(ordenado[^1]),
                Arredonda(amostras.Average()));
        }

        private static double Percentil(List<double> ordenado, double p)
        {
            var indice = (int)Math.Ceiling(p * ordenado.Count) - 1;
            indice = Math.Clamp(indice, 0, ordenado.Count - 1);
            return Arredonda(ordenado[indice]);
        }

        private static double Arredonda(double valor) => Math.Round(valor, 1);

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Rota,-18} n={Amostras} min={MinMs} p50={P50Ms} p90={P90Ms} p95={P95Ms} p99={P99Ms} max={MaxMs} media={MediaMs} (ms)");
    }
}
