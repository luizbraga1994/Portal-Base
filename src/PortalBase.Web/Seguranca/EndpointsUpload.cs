using Microsoft.AspNetCore.Antiforgery;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Endpoint de UPLOAD SEGURO (Prompt 08). Toda a decisão é server-side: exige usuário
/// autenticado, antiforgery válido (seção 24), rate limiting (seção 32) e o tenant é RESOLVIDO
/// NO SERVIDOR a partir do principal autenticado (<see cref="SelecaoTenant"/>) — o
/// <c>tenantId</c> do formulário é apenas o alvo pretendido, validado contra o vínculo do usuário
/// (nunca define a base diretamente — regra 4). O caso de uso <see cref="IngerirArquivo"/> faz
/// tamanho/extensão/conteúdo/hash/antivírus/idempotência/armazenamento/metadados/auditoria. As
/// respostas são GENÉRICAS (sem detalhes internos — regra 16). Não há download público do binário.
/// </summary>
public static class EndpointsUpload
{
    public static IEndpointRouteBuilder MapEndpointsUpload(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        rotas.MapPost("/arquivos/importar", ImportarAsync)
            .RequireAuthorization()
            .RequireRateLimiting(PoliticasWeb.RateUpload)
            .DisableAntiforgery(); // validado explicitamente abaixo (multipart), não pelo middleware.

        return rotas;
    }

    private static async Task<IResult> ImportarAsync(
        HttpContext contexto,
        IAntiforgery antiforgery,
        SelecaoTenant selecaoTenant,
        IngerirArquivo ingerir,
        CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        if (!contexto.Request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);

        // O tenant é o ALVO pretendido; a validação do vínculo é server-side (fail-closed).
        if (!Guid.TryParse(formulario["tenantId"], out var tenantId))
        {
            return Results.BadRequest();
        }

        var arquivo = formulario.Files.GetFile("arquivo");
        if (arquivo is null || arquivo.Length <= 0)
        {
            return Results.BadRequest();
        }

        try
        {
            // Resolve/valida o tenant no servidor (vínculo ativo). Fail-closed: lança se não autorizado.
            await selecaoTenant.SelecionarAsync(tenantId, cancellationToken).ConfigureAwait(false);

            await using var conteudo = arquivo.OpenReadStream();
            var resultado = await ingerir
                .ExecutarAsync(new ComandoIngestao(arquivo.FileName, conteudo), cancellationToken)
                .ConfigureAwait(false);

            return resultado.Situacao switch
            {
                SituacaoIngestao.Aceito => Results.Json(
                    new { situacao = "aceito", arquivoId = resultado.ArquivoId }, statusCode: StatusCodes.Status202Accepted),
                SituacaoIngestao.Duplicado => Results.Json(
                    new { situacao = "duplicado" }, statusCode: StatusCodes.Status200OK),
                _ => Results.Json(
                    new { situacao = "rejeitado", motivo = resultado.Motivo }, statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }
        catch (Exception excecao) when (EhAutorizacao(excecao))
        {
            // Resposta genérica: não revela existência/estado do tenant nem o motivo interno.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    private static bool EhAutorizacao(Exception excecao)
        => excecao is NaoAutenticadoException
            or AutorizacaoNegadaException
            or MfaRequeridoException
            or TenantNaoAutorizadoException
            or TenantNaoEncontradoException
            or TenantContextAusenteException
            or ConexaoTenantNaoEncontradaException;

    private static async Task<bool> ValidarAntiforgeryAsync(HttpContext contexto, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(contexto).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
