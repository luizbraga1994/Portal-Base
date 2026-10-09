using FluentAssertions;
using PortalBase.Web.Ui.Dados;
using PortalBase.Web.Ui.Navegacao;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Testes do gerador de dados de auditoria de demonstração (§36) — filtro, paginação e ordenação —
/// e da promoção do item de menu de Auditoria a tela real (não mais "em preparação").
/// </summary>
public sealed class AuditoriaDemonstracaoTests
{
    [Fact]
    public void Consulta_Sem_Filtro_Retorna_Registros_Rotulados_Como_Demonstracao()
    {
        var r = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm());

        r.Estado.Should().Be(EstadoConsultaAuditoria.Demonstracao);
        r.Total.Should().BeGreaterThan(0);
        r.Itens.Should().NotBeEmpty();
    }

    [Fact]
    public void Filtro_Por_Resultado_Negado_So_Retorna_Negados()
    {
        var r = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm { Resultado = "Negado" });

        r.Itens.Should().OnlyContain(x => x.Resultado == "Negado");
        r.Itens.Should().NotBeEmpty();
    }

    [Fact]
    public void Filtro_Por_Acao_Filtra_Por_Substring()
    {
        var r = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm { Acao = "Upload" });

        r.Itens.Should().OnlyContain(x => x.Acao.Contains("Upload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Paginacao_Respeita_Tamanho_E_Total()
    {
        var pagina1 = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm { Pagina = 1, TamanhoPagina = 5 });
        var pagina2 = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm { Pagina = 2, TamanhoPagina = 5 });

        pagina1.Itens.Should().HaveCount(5);
        pagina1.Total.Should().Be(pagina2.Total);
        pagina2.Itens.Should().NotIntersectWith(pagina1.Itens);
    }

    [Fact]
    public void Ordena_Por_Data_Descendente()
    {
        var r = AuditoriaDemonstracao.Consultar(new FiltroAuditoriaVm { TamanhoPagina = 100 });

        r.Itens.Should().BeInDescendingOrder(x => x.OcorridoEmUtc);
    }

    [Fact]
    public void Auditoria_Nao_E_Mais_Modulo_Em_Preparacao()
    {
        var item = NavegacaoPortal.EncontrarPorRota("/auditoria/eventos");

        item.Should().NotBeNull();
        item!.EmPreparacao.Should().BeFalse();
    }
}
