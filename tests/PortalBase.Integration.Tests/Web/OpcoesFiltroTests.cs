using FluentAssertions;
using PortalBase.Domain.Arquivos;
using PortalBase.Domain.Incidentes;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Filtros de opção DERIVADOS de enums (Prompt 74, §6). Garante que TODO valor de opção enviado ao facade
/// casa com um membro real do enum (o facade faz Enum.Parse) — o teste que teria pego o bug do P73
/// (a tela de validação enviava "Inconclusivo", membro inexistente, e o filtro nunca casava). Puro,
/// determinístico, sem MySQL/navegador.
/// </summary>
public sealed class OpcoesFiltroTests
{
    private static void TodosCasamComEnum<T>(IEnumerable<OpcaoFiltro> opcoes) where T : struct, Enum
    {
        var lista = opcoes.ToList();
        lista.Should().NotBeEmpty();
        foreach (var o in lista)
        {
            o.Valor.Should().NotBeNullOrWhiteSpace();
            o.Rotulo.Should().NotBeNullOrWhiteSpace();
            Enum.TryParse<T>(o.Valor, ignoreCase: false, out _).Should().BeTrue(
                because: $"o valor '{o.Valor}' deve ser um membro real de {typeof(T).Name} (senão o filtro nunca casa)");
        }

        // Sem duplicados de valor.
        lista.Select(x => x.Valor).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Todas_As_Opcoes_Casam_Com_Membros_Reais_Do_Enum()
    {
        TodosCasamComEnum<EstadoArquivo>(OpcoesFiltro.EstadosArquivo);
        TodosCasamComEnum<ResultadoScan>(OpcoesFiltro.ResultadosScan);
        TodosCasamComEnum<SeveridadeIncidente>(OpcoesFiltro.SeveridadesIncidente);
        TodosCasamComEnum<StatusIncidente>(OpcoesFiltro.StatusIncidente);
        TodosCasamComEnum<TipoIncidente>(OpcoesFiltro.TiposIncidente);
    }
}
