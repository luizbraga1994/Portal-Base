using FluentAssertions;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;
using PortalBase.Application.Incidentes;
using PortalBase.Domain.Incidentes;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Prompt 81 — camada de UI do filtro de período (sem bUnit no repositório: testes puros dos helpers que o
/// componente usa, da fachada e um CONTRATO de fonte das páginas). Garante: conversão segura da data digitada
/// (vazio/malformado ⇒ sem limite, nunca exceção), regra única Desde ≤ Até, fachada mapeando o período e
/// convertendo a rejeição do servidor em estado seguro, e que as listas temporais ligam o período enquanto as
/// telas de COMPETÊNCIA mensal continuam sem intervalo arbitrário.
/// </summary>
public sealed class FiltroPeriodoP81UiTests
{
    // ---- Conversão texto ⇄ limite (PeriodoUi) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10/03/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("abc")]
    [InlineData("2026-03-10T00:00:00")]
    public void Interpretar_Vazio_Ou_Malformado_E_Sem_Limite(string? valor)
    {
        PeriodoUi.Interpretar(valor, fimDoDia: false).Should().BeNull();
        PeriodoUi.Interpretar(valor, fimDoDia: true).Should().BeNull();
    }

    [Fact]
    public void Interpretar_Desde_E_Inicio_Do_Dia_E_Ate_E_Fim_Do_Dia_Em_Utc()
    {
        var desde = PeriodoUi.Interpretar("2026-03-10", fimDoDia: false);
        var ate = PeriodoUi.Interpretar("2026-03-10", fimDoDia: true);

        desde.Should().Be(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero));
        ate.Should().Be(PeriodoConsulta.FimDoDia(new DateOnly(2026, 3, 10)));
        ate!.Value.Offset.Should().Be(TimeSpan.Zero);
        PeriodoUi.EhValido(desde, ate).Should().BeTrue(); // mesma data é válida
    }

    [Fact]
    public void Formatar_Faz_Ida_E_Volta_Do_Input_Date()
    {
        PeriodoUi.Formatar(null).Should().BeEmpty();
        PeriodoUi.Formatar(PeriodoUi.Interpretar("2026-03-12", fimDoDia: true)).Should().Be("2026-03-12");
        PeriodoUi.Formatar(PeriodoUi.Interpretar("2026-03-10", fimDoDia: false)).Should().Be("2026-03-10");
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("2026-03-10", null, true)]
    [InlineData(null, "2026-03-12", true)]
    [InlineData("2026-03-10", "2026-03-12", true)]
    [InlineData("2026-03-12", "2026-03-12", true)]
    [InlineData("2026-03-13", "2026-03-12", false)]
    public void EhValido_Segue_A_Regra_Unica_Do_Servidor(string? desde, string? ate, bool esperado)
    {
        var d = PeriodoUi.Interpretar(desde, fimDoDia: false);
        var a = PeriodoUi.Interpretar(ate, fimDoDia: true);

        PeriodoUi.EhValido(d, a).Should().Be(esperado).And.Be(PeriodoConsulta.EhValido(d, a));
    }

    // ---- Auditoria em DEMONSTRAÇÃO aplica o período como o backend ----

    [Fact]
    public void Demonstracao_Filtra_Por_Periodo_Antes_De_Paginar()
    {
        var desde = new DateTimeOffset(2025, 8, 20, 14, 10, 0, TimeSpan.Zero);
        var ate = new DateTimeOffset(2025, 8, 20, 14, 30, 0, TimeSpan.Zero);

        var r = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm { Desde = desde, Ate = ate, TamanhoPagina = 2 });

        r.Total.Should().Be(3);
        r.Itens.Should().HaveCount(2).And.OnlyContain(x => x.OcorridoEmUtc >= desde && x.OcorridoEmUtc <= ate);
        r.Estado.Should().Be(EstadoConsultaAuditoria.Demonstracao);
    }

    [Fact]
    public void Demonstracao_Periodo_Invertido_E_Erro_Sem_Itens()
    {
        var r = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm
        {
            Desde = new DateTimeOffset(2025, 8, 21, 0, 0, 0, TimeSpan.Zero),
            Ate = new DateTimeOffset(2025, 8, 20, 0, 0, 0, TimeSpan.Zero),
        });

        r.Estado.Should().Be(EstadoConsultaAuditoria.Erro);
        r.Itens.Should().BeEmpty();
        r.Total.Should().Be(0);
    }

    // ---- Fachada: período chega ao serviço; rejeição do servidor vira estado SEGURO (sem exceção) ----

    [Fact]
    public async Task Fachada_Incidentes_Repassa_Periodo_Ao_Servico()
    {
        var consulta = new ConsultaIncidentesCaptura();
        var dados = new DadosIncidentes(new ConsultaIncidentesAutorizada(new PlataformaPermiteTudo(), consulta, new RepositorioIncidentesVazio()));
        var desde = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
        var ate = PeriodoConsulta.FimDoDia(new DateOnly(2026, 3, 12));

        var r = await dados.ConsultarAsync(new FiltroIncidentesVm { Desde = desde, Ate = ate, Severidade = "Alto" });

        r.Estado.Should().Be(EstadoConsultaUi.Ok);
        consulta.UltimoFiltro!.Desde.Should().Be(desde);
        consulta.UltimoFiltro.Ate.Should().Be(ate);
        consulta.UltimoFiltro.Severidade.Should().Be(SeveridadeIncidente.Alto);
    }

    [Fact]
    public async Task Fachada_Incidentes_Periodo_Invertido_E_Estado_De_Erro_Seguro_Sem_Consultar()
    {
        var consulta = new ConsultaIncidentesCaptura();
        var dados = new DadosIncidentes(new ConsultaIncidentesAutorizada(new PlataformaPermiteTudo(), consulta, new RepositorioIncidentesVazio()));

        var r = await dados.ConsultarAsync(new FiltroIncidentesVm
        {
            Desde = new DateTimeOffset(2026, 3, 13, 0, 0, 0, TimeSpan.Zero),
            Ate = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero),
        });

        r.Estado.Should().Be(EstadoConsultaUi.Erro); // sem stack trace/HTTP 500: estado seguro
        r.Itens.Should().BeEmpty();
        consulta.UltimoFiltro.Should().BeNull();     // a porta de leitura NÃO foi chamada
    }

    // ---- Contrato de fonte das páginas (não há bUnit: o contrato evita regressão silenciosa da ligação) ----

    public static TheoryData<string, string> PaginasComPeriodo => new()
    {
        { "Auditoria/Eventos.razor", "FiltroAuditoriaVm" },
        { "Auditoria/OperacoesAuditoria.razor", "FiltroAuditoriaVm" },
        { "Auditoria/Incidentes.razor", "FiltroIncidentesVm" },
        { "Dados/Arquivos.razor", "FiltroArquivosVm" },
    };

    [Theory]
    [MemberData(nameof(PaginasComPeriodo))]
    public void Lista_Temporal_Liga_O_Periodo_E_Nao_Consulta_Se_Invalido(string pagina, string filtroVm)
    {
        var fonte = LerPagina(pagina);

        fonte.Should().Contain("<FiltroPeriodo Rotulo=", "o período deve estar na barra de filtros com rótulo explícito");
        fonte.Should().Contain("Desde = _desde", "o período digitado precisa chegar ao " + filtroVm);
        fonte.Should().Contain("_desde = _ate = null", "Limpar deve zerar o período");
        fonte.Should().Contain("if (!PeriodoUi.EhValido(_desde, _ate))", "Desde > Até não pode consultar");
        fonte.Should().Contain("disabled=\"@(!PeriodoUi.EhValido(_desde, _ate))\"", "o botão Filtrar fica bloqueado");
    }

    [Fact]
    public void Componente_Exibe_Validacao_Acessivel_Sem_Estilo_Inline()
    {
        var fonte = File.ReadAllText(Path.Combine(RaizWeb(), "Components", "UI", "FiltroPeriodo.razor"));

        fonte.Should().Contain("role=\"alert\"");
        fonte.Should().Contain("aria-invalid=");
        fonte.Should().Contain("PeriodoConsulta.MensagemPeriodoInvalido");
        fonte.Should().NotContain("style=");
    }

    private static string LerPagina(string relativo)
    {
        var caminho = Path.Combine(RaizWeb(), "Components", "Pages", relativo.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(caminho).Should().BeTrue($"a página {relativo} deve existir");
        return File.ReadAllText(caminho);
    }

    private static string RaizWeb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("os testes rodam dentro do repositório");
        return Path.Combine(dir!.FullName, "src", "PortalBase.Web");
    }

    private sealed class PlataformaPermiteTudo : IAutorizacaoPlataforma
    {
        public ValueTask<ResultadoAutorizacao> AvaliarAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ResultadoAutorizacao.Ok);

        public ValueTask ExigirAsync(string permissaoGlobal, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class ConsultaIncidentesCaptura : IConsultaIncidentes
    {
        public FiltroIncidentes? UltimoFiltro { get; private set; }

        public Task<ResultadoPaginado<ItemIncidente>> ConsultarAsync(FiltroIncidentes filtro, CancellationToken cancellationToken = default)
        {
            UltimoFiltro = filtro;
            return Task.FromResult(ResultadoPaginado.Vazio<ItemIncidente>(filtro.Pagina, filtro.TamanhoPagina));
        }
    }

    private sealed class RepositorioIncidentesVazio : IRepositorioIncidentes
    {
        public Task AdicionarAsync(IncidenteSeguranca incidente, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IncidenteSeguranca?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<IncidenteSeguranca?>(null);

        public Task<IncidenteSeguranca?> ObterPorCodigoAsync(string codigo, CancellationToken cancellationToken = default)
            => Task.FromResult<IncidenteSeguranca?>(null);

        public Task<IReadOnlyList<IncidenteSeguranca>> ObterRecentesAsync(int limite, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IncidenteSeguranca>>([]);

        public Task SalvarAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
