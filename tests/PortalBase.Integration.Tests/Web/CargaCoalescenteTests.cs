using FluentAssertions;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// REG-001 (auditoria de 08/10/2026) — teste permanente: a tela pode pedir várias cargas (filtrar, paginar, recarregar
/// depois de uma ação) enquanto uma ainda está em andamento. Nunca duas consultas rodam ao mesmo tempo (o DbContext do
/// circuito não aceita), nenhum pedido se perde e só o resultado mais recente é exibido.
/// </summary>
public sealed class CargaCoalescenteTests
{
    [Fact]
    public async Task Pedido_Durante_A_Carga_Repete_A_Consulta_Com_O_Filtro_Atual_Sem_Sobrepor()
    {
        var carga = new CargaCoalescente();
        var filtro = "A";
        var emCurso = 0;
        var maximo = 0;
        var consultados = new List<string>();
        var aplicados = new List<string>();
        var primeira = new TaskCompletionSource<string>();

        async Task<string> Consultar()
        {
            var atual = filtro;
            consultados.Add(atual);
            maximo = Math.Max(maximo, ++emCurso);
            try
            {
                return consultados.Count == 1 ? await primeira.Task : atual;
            }
            finally
            {
                emCurso--;
            }
        }

        var t1 = carga.ExecutarAsync(Consultar, r => aplicados.Add(r));
        carga.EmAndamento.Should().BeTrue();

        // O usuário troca o filtro duas vezes durante a carga: os pedidos voltam na hora, sem nova consulta paralela.
        filtro = "B";
        var t2 = carga.ExecutarAsync(Consultar, r => aplicados.Add(r));
        filtro = "C";
        var t3 = carga.ExecutarAsync(Consultar, r => aplicados.Add(r));
        t2.IsCompleted.Should().BeTrue();
        t3.IsCompleted.Should().BeTrue();
        consultados.Should().Equal("A");

        primeira.SetResult("A");
        await t1;

        consultados.Should().Equal("A", "C");
        aplicados.Should().Equal(["C"], "o resultado velho (A) não é exibido; só o do filtro atual");
        maximo.Should().Be(1, "nunca duas consultas ao mesmo tempo no mesmo DbContext");
        carga.EmAndamento.Should().BeFalse();
    }

    [Fact]
    public async Task Sem_Pedidos_No_Meio_Consulta_Uma_Vez_E_Aplica()
    {
        var carga = new CargaCoalescente();
        var chamadas = 0;
        string? aplicado = null;

        await carga.ExecutarAsync(() => { chamadas++; return Task.FromResult("ok"); }, r => aplicado = r);

        chamadas.Should().Be(1);
        aplicado.Should().Be("ok");
        carga.EmAndamento.Should().BeFalse();
    }

    [Fact]
    public async Task Falha_Na_Consulta_Libera_A_Proxima_Carga()
    {
        var carga = new CargaCoalescente();

        var falha = async () => await carga.ExecutarAsync<string>(() => throw new InvalidOperationException("banco"), _ => { });
        await falha.Should().ThrowAsync<InvalidOperationException>();
        carga.EmAndamento.Should().BeFalse();

        string? aplicado = null;
        await carga.ExecutarAsync(() => Task.FromResult("depois"), r => aplicado = r);
        aplicado.Should().Be("depois");
    }
}
