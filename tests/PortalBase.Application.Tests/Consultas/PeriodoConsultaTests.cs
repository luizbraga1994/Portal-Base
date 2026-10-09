using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Incidentes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Tests.Consultas;

/// <summary>
/// Prompt 81 — regra ÚNICA de período (<see cref="PeriodoConsulta"/>) e sua aplicação SERVER-SIDE nos serviços
/// de consulta autorizados: <c>Desde &gt; Até</c> é rejeitado ANTES de chegar à porta de leitura (a UI só
/// pré-valida), limites ausentes não limitam, e o período chega intacto (com os demais filtros) à consulta.
/// </summary>
public sealed class PeriodoConsultaTests
{
    private static readonly DateTimeOffset Dia1 = new(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Dia2 = new(2026, 3, 20, 0, 0, 0, TimeSpan.Zero);

    private static ContextoTenantFake TenantAutorizado() => new() { TenantId = Guid.NewGuid() };

    // ---- Regra pura ----

    public static TheoryData<DateTimeOffset?, DateTimeOffset?, bool> Casos => new()
    {
        { null, null, true },   // nenhum limite
        { Dia1, null, true },   // só Desde
        { null, Dia2, true },   // só Até
        { Dia1, Dia2, true },   // Desde < Até
        { Dia1, Dia1, true },   // mesmo instante (inclusivo)
        { Dia2, Dia1, false },  // Desde > Até
    };

    [Theory]
    [MemberData(nameof(Casos))]
    public void EhValido_Cobre_Todas_As_Combinacoes(DateTimeOffset? desde, DateTimeOffset? ate, bool esperado)
        => PeriodoConsulta.EhValido(desde, ate).Should().Be(esperado);

    [Fact]
    public void Validar_Desde_Maior_Que_Ate_Lanca_Entrada_Invalida_Com_Mensagem_Segura()
    {
        var acao = () => PeriodoConsulta.Validar(Dia2, Dia1);

        acao.Should().Throw<EntradaInvalidaException>().WithMessage(PeriodoConsulta.MensagemPeriodoInvalido);
    }

    [Fact]
    public void Validar_Periodo_Valido_Nao_Lanca()
    {
        var valido = () => PeriodoConsulta.Validar(Dia1, Dia2);
        var semLimites = () => PeriodoConsulta.Validar(null, null);

        valido.Should().NotThrow();
        semLimites.Should().NotThrow();
    }

    [Fact]
    public void Inicio_E_Fim_Do_Dia_Sao_Utc_E_Inclusivos()
    {
        var dia = new DateOnly(2026, 3, 10);

        var inicio = PeriodoConsulta.InicioDoDia(dia);
        var fim = PeriodoConsulta.FimDoDia(dia);

        inicio.Should().Be(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero));
        inicio.Offset.Should().Be(TimeSpan.Zero);
        fim.Offset.Should().Be(TimeSpan.Zero);
        fim.Should().Be(new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero).AddTicks(-TimeSpan.TicksPerMicrosecond));
        // Mesma data em Desde e Até é um período VÁLIDO que cobre o dia inteiro (inclusive a fração final).
        PeriodoConsulta.EhValido(inicio, fim).Should().BeTrue();
        (new DateTimeOffset(2026, 3, 10, 23, 59, 59, TimeSpan.Zero).AddMilliseconds(500) <= fim).Should().BeTrue();
    }

    [Fact]
    public void Datas_Em_Fusos_Diferentes_Comparam_Pelo_Instante()
    {
        // 10/03 02:00 em -03:00 = 10/03 05:00Z, posterior a 10/03 04:00Z: inválido como Desde > Até.
        var desde = new DateTimeOffset(2026, 3, 10, 2, 0, 0, TimeSpan.FromHours(-3));
        var ate = new DateTimeOffset(2026, 3, 10, 4, 0, 0, TimeSpan.Zero);

        PeriodoConsulta.EhValido(desde, ate).Should().BeFalse();
        PeriodoConsulta.EhValido(ate, desde).Should().BeTrue();
    }

    // ---- Serviços autorizados: rejeição server-side SEM chamar a porta ----

    [Fact]
    public async Task Incidentes_Periodo_Invertido_E_Rejeitado_Sem_Consultar()
    {
        var consulta = new ConsultaIncidentesFake();
        var servico = new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake(), consulta, new RepositorioIncidentesFake());

        var acao = async () => await servico.ConsultarAsync(new FiltroIncidentes { Desde = Dia2, Ate = Dia1 });

        await acao.Should().ThrowAsync<EntradaInvalidaException>();
        consulta.UltimoFiltro.Should().BeNull();
    }

    [Fact]
    public async Task Arquivos_Periodo_Invertido_E_Rejeitado_Sem_Consultar()
    {
        var consulta = new ConsultaArquivosImportacaoFake();
        var servico = new ConsultaArquivosImportacaoAutorizada(TenantAutorizado(), new ServicoAutorizacaoFake(), consulta);

        var acao = async () => await servico.ConsultarAsync(new FiltroArquivosImportacao { Desde = Dia2, Ate = Dia1 });

        await acao.Should().ThrowAsync<EntradaInvalidaException>();
        consulta.UltimoFiltro.Should().BeNull();
    }

    [Fact] // A autorização vem ANTES da validação: sem permissão nada é revelado sobre a entrada.
    public async Task Sem_Permissao_Nega_Antes_De_Validar_O_Periodo()
    {
        var consulta = new ConsultaArquivosImportacaoFake();
        var servico = new ConsultaArquivosImportacaoAutorizada(TenantAutorizado(), new ServicoAutorizacaoFake { Permitir = false }, consulta);

        var acao = async () => await servico.ConsultarAsync(new FiltroArquivosImportacao { Desde = Dia2, Ate = Dia1 });

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        consulta.UltimoFiltro.Should().BeNull();
    }

    [Fact]
    public async Task Incidentes_Arquivos_Mesmo_Dia_E_Aceito()
    {
        var dia = new DateOnly(2026, 3, 10);
        var (desde, ate) = (PeriodoConsulta.InicioDoDia(dia), PeriodoConsulta.FimDoDia(dia));

        var inc = new ConsultaIncidentesFake();
        await new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake(), inc, new RepositorioIncidentesFake())
            .ConsultarAsync(new FiltroIncidentes { Desde = desde, Ate = ate });
        var arq = new ConsultaArquivosImportacaoFake();
        await new ConsultaArquivosImportacaoAutorizada(TenantAutorizado(), new ServicoAutorizacaoFake(), arq)
            .ConsultarAsync(new FiltroArquivosImportacao { Desde = desde, Ate = ate });

        inc.UltimoFiltro!.Desde.Should().Be(desde);
        inc.UltimoFiltro.Ate.Should().Be(ate);
        arq.UltimoFiltro!.Desde.Should().Be(desde);
        arq.UltimoFiltro.Ate.Should().Be(ate);
    }
}

/// <summary>Prompt 81 — auditoria: período invertido é rejeitado no servidor, sem consultar e sem auditar acesso.</summary>
public sealed class PeriodoConsultaAuditoriaTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid Usuario = Guid.NewGuid();

    private readonly ContextoSeguranca _seguranca = new();
    private readonly AutorizacaoTenantFakeIdentidade _tenant = new();
    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly ConsultaAuditoriaFake _consulta = new();
    private readonly AuditoriaFake _auditoria = new();
    private readonly ConsultaAuditoriaAutorizada _servico;

    public PeriodoConsultaAuditoriaTests()
    {
        var autorizacao = new ServicoAutorizacao(_seguranca, _tenant, _usuarios, _auditoria, new RelogioFixo(Momento));
        _servico = new ConsultaAuditoriaAutorizada(autorizacao, _consulta, new ConsultaNomesAtoresFake(), _seguranca, _auditoria, new RelogioFixo(Momento));
        _seguranca.Definir(Usuario.ToString(), mfaSatisfeito: false);
        _tenant.Permitidos.Add((Usuario.ToString(), TenantA));
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.AuditoriaVisualizar);
    }

    [Fact]
    public async Task Periodo_Invertido_E_Rejeitado_Sem_Consultar_Nem_Registrar_AuditQueried()
    {
        var filtro = new FiltroAuditoria { Tenant = "x", Desde = Momento, Ate = Momento.AddDays(-1) };

        var acao = async () => await _servico.ConsultarAsync(TenantA, filtro);

        await acao.Should().ThrowAsync<EntradaInvalidaException>();
        _consulta.UltimoFiltro.Should().BeNull();
        _auditoria.Eventos.Should().NotContain(e => e.Operacao == AcaoAuditoria.AuditQueried);
    }

    [Fact]
    public async Task Periodo_Valido_Chega_A_Consulta_Com_Tenant_Forcado()
    {
        var filtro = new FiltroAuditoria { Tenant = "bypass", Desde = Momento.AddDays(-7), Ate = Momento, Acao = "TenantActivated" };

        await _servico.ConsultarAsync(TenantA, filtro);

        _consulta.UltimoFiltro!.Desde.Should().Be(Momento.AddDays(-7));
        _consulta.UltimoFiltro.Ate.Should().Be(Momento);
        _consulta.UltimoFiltro.Acao.Should().Be("TenantActivated");
        _consulta.UltimoFiltro.Tenant.Should().Be(TenantA.ToString());
    }
}
