using FluentAssertions;
using PortalBase.Application.Consultas;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tests.Dobras;

namespace PortalBase.Application.Tests.Ingestao;

/// <summary>Read-model de arquivos AUTORIZADO (Prompt 36, §15-17, §60-61).</summary>
public sealed class ConsultasArquivosTests
{
    private static ContextoTenantFake TenantAutorizado() => new() { TenantId = Guid.NewGuid() };

    private static ContextoTenantFake TenantSemContexto() => new() { TenantId = null };

    // ---- Arquivos de importação (upload) ----

    [Fact]
    public async Task Arquivos_Sem_Permissao_Nega()
    {
        var servico = new ConsultaArquivosImportacaoAutorizada(TenantAutorizado(), new ServicoAutorizacaoFake { Permitir = false }, new ConsultaArquivosImportacaoFake());
        var acao = async () => await servico.ConsultarAsync(new FiltroArquivosImportacao());
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact]
    public async Task Arquivos_Autorizado_Normaliza_Paginacao()
    {
        var consulta = new ConsultaArquivosImportacaoFake();
        var servico = new ConsultaArquivosImportacaoAutorizada(TenantAutorizado(), new ServicoAutorizacaoFake(), consulta);

        await servico.ConsultarAsync(new FiltroArquivosImportacao { TamanhoPagina = 0 });

        consulta.UltimoFiltro!.TamanhoPagina.Should().Be(Paginacao.TamanhoPadrao);
    }

}
