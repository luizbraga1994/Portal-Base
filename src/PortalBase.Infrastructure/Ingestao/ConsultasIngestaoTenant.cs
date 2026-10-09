using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Consultas;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Arquivos;
using PortalBase.Infrastructure.Consultas;
using PortalBase.Infrastructure.Multitenancy;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>Read-model de ARQUIVOS DE IMPORTAÇÃO / uploads (Prompt 36, §15) — só metadados (nunca conteúdo/ref).</summary>
public sealed class ConsultaArquivosImportacaoTenant : ConsultaTenantBase, IConsultaArquivosImportacao
{
    public ConsultaArquivosImportacaoTenant(IFabricaContextoTenant fabrica, ITenantContext tenant)
        : base(fabrica, tenant)
    {
    }

    public async Task<ResultadoPaginado<ItemArquivoImportacao>> ConsultarAsync(FiltroArquivosImportacao filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var f = filtro.Normalizado();
        var contexto = await ContextoAsync(cancellationToken).ConfigureAwait(false);

        var consulta = contexto.ArquivosImportacao.AsNoTracking();

        if (f.Estado is EstadoArquivo estado)
        {
            consulta = consulta.Where(x => x.Estado == estado);
        }

        if (f.ResultadoScan is ResultadoScan scan)
        {
            consulta = consulta.Where(x => x.ResultadoScan == scan);
        }

        if (!string.IsNullOrWhiteSpace(f.Extensao))
        {
            var ext = f.Extensao.Trim();
            consulta = consulta.Where(x => x.Extensao == ext);
        }

        if (!string.IsNullOrWhiteSpace(f.Sha256))
        {
            var hash = f.Sha256.Trim();
            consulta = consulta.Where(x => x.Sha256 == hash);
        }

        // Período (Prompt 81) aplicado NO BANCO, antes da contagem, da ordenação e do Skip/Take.
        if (f.Desde is { } desde)
        {
            consulta = consulta.Where(x => x.EnviadoEmUtc >= desde);
        }

        if (f.Ate is { } ate)
        {
            consulta = consulta.Where(x => x.EnviadoEmUtc <= ate);
        }

        var total = await consulta.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var asc = f.Direcao == DirecaoOrdenacao.Ascendente;
        consulta = f.Ordenacao switch
        {
            CampoOrdenacaoArquivoImportacao.Estado => asc
                ? consulta.OrderBy(x => x.Estado).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.Estado).ThenBy(x => x.Id),
            CampoOrdenacaoArquivoImportacao.Tamanho => asc
                ? consulta.OrderBy(x => x.TamanhoBytes).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.TamanhoBytes).ThenBy(x => x.Id),
            _ => asc
                ? consulta.OrderBy(x => x.EnviadoEmUtc).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.EnviadoEmUtc).ThenBy(x => x.Id),
        };

        var itens = await consulta
            .Skip(Paginacao.Saltar(f.Pagina, f.TamanhoPagina))
            .Take(f.TamanhoPagina)
            .Select(x => new ItemArquivoImportacao(
                x.Id, x.NomeOriginalSanitizado, x.Extensao, x.ContentTypeDetectado, x.TamanhoBytes,
                x.Sha256, x.Estado, x.ResultadoScan, x.EscaneadoEmUtc, x.MotivoRejeicao, x.EnviadoEmUtc, x.AtualizadoEmUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ResultadoPaginado<ItemArquivoImportacao>(itens, f.Pagina, f.TamanhoPagina, total);
    }
}
