using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Marca;
using PortalBase.Domain.Marca;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// UI-005 (auditoria de 08/10/2026), contra o MySQL REAL: o resumo da logo (o que o layout usa a cada navegação) é UMA
/// consulta que NÃO traz a coluna da imagem, e a navegação seguinte vem do cache — antes, cada página lia a imagem
/// inteira (até 10 MB) do banco. Imagem fictícia (PNG mínimo + bytes de enchimento). Pulado sem MySQL de teste.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class MarcaResumoMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public MarcaResumoMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Resumo_Da_Logo_Global_Nao_Le_A_Imagem_E_A_Proxima_Navegacao_Nao_Vai_Ao_Banco()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var imagem = new byte[3 * 1024 * 1024];
        byte[] cabecalhoPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        cabecalhoPng.CopyTo(imagem, 0);
        Random.Shared.NextBytes(imagem.AsSpan(cabecalhoPng.Length));
        string hash;
        using (var escopo = _fixture.CriarEscopo())
        {
            var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioMarcaPlataforma>();
            var marca = await repositorio.ObterAsync();
            if (marca is null)
            {
                marca = MarcaVisual.Criar(imagem, "image/png", DateTimeOffset.UtcNow, null);
            }
            else
            {
                marca.Atualizar(imagem, "image/png", DateTimeOffset.UtcNow, null);
            }

            await repositorio.SalvarAsync(marca);
            hash = marca.HashSha256;
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            escopo.ServiceProvider.GetRequiredService<CacheResumoMarca>().Invalidar(ServicoMarcaPlataforma.ChaveCache);
            var servico = escopo.ServiceProvider.GetRequiredService<ServicoMarcaPlataforma>();

            using var medidor = new MedidorDesempenho(_fixture.CatalogoDb);
            var primeira = await servico.ObterResumoAsync();
            var segunda = await servico.ObterResumoAsync();
            var medicao = medidor.Parar("resumo da logo");

            primeira.Should().Be(new ResumoMarcaVisual("image/png", hash, imagem.Length));
            segunda.Should().Be(primeira);
            medicao.Selects.Should().Be(1, "a segunda navegação vem do cache");
            medidor.TextosComandos.Should().NotContain(t => t.Contains("Conteudo", StringComparison.OrdinalIgnoreCase),
                "o resumo nunca traz a coluna da imagem");

            // Prova de que o medidor enxerga a coluna quando ela é lida (a imagem só é lida para servir de fato).
            using var medidorImagem = new MedidorDesempenho(_fixture.CatalogoDb);
            (await servico.ObterAsync())!.Conteudo.Should().HaveCount(imagem.Length);
            medidorImagem.TextosComandos.Should().Contain(t => t.Contains("Conteudo", StringComparison.OrdinalIgnoreCase));
        }
    }
}
