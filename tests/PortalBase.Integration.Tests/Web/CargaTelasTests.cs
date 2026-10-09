using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// UI-003 (auditoria de 08/10/2026) — Auditoria → Eventos, Operações de auditoria e Configuração fiscal carregavam sem
/// guarda: filtrar, paginar ou limpar durante uma carga disparava uma SEGUNDA consulta no mesmo DbContext do circuito,
/// que não aceita duas operações ao mesmo tempo ("Não foi possível carregar" intermitente, como no REG-001). Aqui: a
/// causa no MySQL real, a mesma sequência passando pela <see cref="CargaCoalescente"/> e a varredura que impede uma
/// tela de lista nova (ou alterada) sem guarda.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed partial class CargaTelasTests
{
    private static readonly string[] TelasUi003 =
    [
        "Auditoria/Eventos.razor",
        "Auditoria/OperacoesAuditoria.razor",
    ];

    private readonly FixtureMultiTenant _fixture;

    public CargaTelasTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Duas_Consultas_Sobrepostas_No_Contexto_Do_Circuito_Falham_E_Pela_Carga_Coalescente_Nao()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        // Causa (o "antes" das telas sem guarda): um escopo = um circuito; duas cargas sobrepostas no mesmo contexto.
        using (var escopo = _fixture.CriarEscopo())
        {
            var contexto = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var sobrepostas = async () => await Task.WhenAll(
                contexto.RegistrosAuditoria.AsNoTracking().CountAsync(),
                contexto.RegistrosAuditoria.AsNoTracking().CountAsync());

            await sobrepostas.Should().ThrowAsync<InvalidOperationException>("o DbContext não aceita duas operações ao mesmo tempo");
        }

        // Correção: as mesmas três cargas pedidas em sequência rápida passam pela carga coalescente — nenhuma falha,
        // nunca duas ao mesmo tempo, e a tela fica com o resultado do último pedido.
        using (var escopo = _fixture.CriarEscopo())
        {
            var contexto = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var carga = new CargaCoalescente();
            var pagina = 1;
            var emCurso = 0;
            var maximo = 0;
            var aplicadas = new List<int>();

            async Task<int> Consultar()
            {
                var pedida = pagina;
                maximo = Math.Max(maximo, ++emCurso);
                try
                {
                    await contexto.RegistrosAuditoria.AsNoTracking().OrderBy(r => r.Id).Skip((pedida - 1) * 25).Take(25).CountAsync();
                    return pedida;
                }
                finally
                {
                    emCurso--;
                }
            }

            var primeira = carga.ExecutarAsync(Consultar, aplicadas.Add);
            pagina = 2;
            var segunda = carga.ExecutarAsync(Consultar, aplicadas.Add);
            pagina = 3;
            var terceira = carga.ExecutarAsync(Consultar, aplicadas.Add);
            await Task.WhenAll(primeira, segunda, terceira);

            maximo.Should().Be(1);
            aplicadas.Should().Equal(3);
        }
    }

    [Fact]
    public void Telas_Da_Ui003_Carregam_Pela_Carga_Coalescente()
    {
        foreach (var tela in TelasUi003)
        {
            var codigo = Codigo(Path.Combine(DiretorioPaginas(), tela));
            codigo.Should().Contain("_carga.ExecutarAsync(", $"{tela} precisa carregar pela CargaCoalescente (UI-003)");
            codigo.Should().NotContain("_carregando = true", $"{tela} não pode voltar ao indicador manual sem guarda");
        }
    }

    [Fact]
    public void Nenhuma_Tela_De_Lista_Carrega_Sem_Guarda()
    {
        var semGuarda = new List<string>();
        foreach (var arquivo in Directory.EnumerateFiles(DiretorioPaginas(), "*.razor", SearchOption.AllDirectories))
        {
            var codigo = Codigo(arquivo);
            var lista = Navegacao().IsMatch(codigo);
            var carrega = codigo.Contains("_carregando", StringComparison.Ordinal) || codigo.Contains("_carga.", StringComparison.Ordinal);
            if (!lista || !carrega)
            {
                continue;
            }

            var coalescente = codigo.Contains("_carga.ExecutarAsync(", StringComparison.Ordinal);
            var guarda = Guarda().IsMatch(codigo);
            if (!coalescente && !guarda)
            {
                semGuarda.Add(Path.GetRelativePath(DiretorioPaginas(), arquivo));
            }
        }

        semGuarda.Should().BeEmpty(
            "tela com filtro/paginação precisa de CargaCoalescente (ou, no mínimo, de 'if (_carregando) return'): sem guarda, duas cargas " +
            "disputam o DbContext do circuito. Sem guarda: " + string.Join(", ", semGuarda));
    }

    private static string Codigo(string arquivo)
    {
        var texto = File.ReadAllText(arquivo);
        var inicio = texto.IndexOf("@code", StringComparison.Ordinal);
        return inicio < 0 ? texto : texto[inicio..];
    }

    private static string DiretorioPaginas()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada."), "src", "PortalBase.Web", "Components", "Pages");
    }

    [GeneratedRegex(@"Task (Filtrar|Proxima|Anterior)\(")]
    private static partial Regex Navegacao();

    [GeneratedRegex(@"if\s*\(\s*_carregando\w*\s*(\|\||\))")]
    private static partial Regex Guarda();
}
