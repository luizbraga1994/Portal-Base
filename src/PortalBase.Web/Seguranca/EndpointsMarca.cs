using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Net.Http.Headers;
using PortalBase.Application.Marca;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Marca;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Endpoints da MARCA VISUAL (logo configurável). SERVIR a imagem por HTTP (onde há cookie/HttpContext):
/// a logo GLOBAL é pública (aparece no login); a logo do CONTRIBUINTE é servida após resolver o tenant da
/// sessão no servidor. ATUALIZAR (upload) é multipart, com antiforgery validado manualmente, rate limit e
/// autorização de PLATAFORMA (global) ou de TENANT (contribuinte) no caso de uso — fail-closed e auditado.
/// O tipo do arquivo é determinado pelos MAGIC BYTES (nunca pelo que o cliente declara — regra 2/10).
/// </summary>
public static class EndpointsMarca
{
    private const string CampoArquivo = "arquivo";

    public static IEndpointRouteBuilder MapEndpointsMarca(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        // Servir — GET. A logo global é anônima (por omissão de RequireAuthorization); a do contribuinte exige sessão.
        rotas.MapGet("/branding/logo", ServirLogoGlobalAsync);
        rotas.MapGet("/branding/logo/contribuinte", ServirLogoContribuinteAsync).RequireAuthorization();

        // Atualizar — POST multipart. Antiforgery validado no handler (por isso DisableAntiforgery), rate limit.
        var grupo = rotas.MapGroup("/admin/marca");
        grupo.MapPost("/plataforma", DefinirLogoPlataformaAsync)
            .RequireAuthorization().RequireRateLimiting(PoliticasWeb.RateUpload).DisableAntiforgery();
        grupo.MapPost("/contribuinte", DefinirLogoContribuinteAsync)
            .RequireAuthorization().RequireRateLimiting(PoliticasWeb.RateUpload).DisableAntiforgery();

        return rotas;
    }

    private static async Task<IResult> ServirLogoGlobalAsync(HttpContext contexto, ServicoMarcaPlataforma servico, CancellationToken cancellationToken)
    {
        // UI-005: primeiro só os metadados (cache curto); a imagem só é lida do banco se o navegador não a tiver.
        var resumo = await servico.ObterResumoAsync(cancellationToken).ConfigureAwait(false);
        if (NaoModificada(contexto, resumo, publico: true) is { } naoModificada)
        {
            return naoModificada;
        }

        var marca = await servico.ObterAsync(cancellationToken).ConfigureAwait(false);
        return Servir(contexto, marca, publico: true);
    }

    private static async Task<IResult> ServirLogoContribuinteAsync(
        HttpContext contexto, IResolvedorTenant resolvedor, ServicoMarcaTenant servico, CancellationToken cancellationToken)
    {
        var principal = contexto.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(contexto.User.FindFirstValue(ClaimsPortalBase.TenantSelecionado), out var tenantId))
        {
            return Results.NotFound();
        }

        try
        {
            await resolvedor.ResolverAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Results.NotFound(); // fail-closed: sem tenant autorizado, nenhuma logo é servida
        }

        var resumo = await servico.ObterResumoAsync(cancellationToken).ConfigureAwait(false);
        if (NaoModificada(contexto, resumo, publico: false) is { } naoModificada)
        {
            return naoModificada;
        }

        var marca = await servico.ObterAsync(cancellationToken).ConfigureAwait(false);
        return Servir(contexto, marca, publico: false);
    }

    /// <summary>
    /// 304 quando o navegador já tem a MESMA imagem (If-None-Match igual ao hash) — sem ler os bytes do banco. Nulo
    /// quando é preciso servir (sem logo, sem cabeçalho ou outra versão).
    /// </summary>
    internal static IResult? NaoModificada(HttpContext contexto, ResumoMarcaVisual? resumo, bool publico)
    {
        if (resumo is null)
        {
            return null;
        }

        var etag = new EntityTagHeaderValue($"\"{resumo.HashSha256}\"");
        var pedidos = contexto.Request.GetTypedHeaders().IfNoneMatch;
        if (pedidos is null || !pedidos.Any(e => e.Equals(EntityTagHeaderValue.Any) || e.Compare(etag, useStrongComparison: false)))
        {
            return null;
        }

        contexto.Response.Headers.CacheControl = publico ? "public, max-age=300" : "private, max-age=60";
        contexto.Response.Headers.ETag = etag.ToString();
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    private static IResult Servir(HttpContext contexto, MarcaVisualView? marca, bool publico)
    {
        if (marca is null)
        {
            return Results.NotFound();
        }

        // ETag = hash do conteúdo: com If-None-Match igual, a resposta é 304 sem corpo — uma logo grande (até 10 MB) não
        // é baixada de novo a cada página, só quando muda.
        contexto.Response.Headers.CacheControl = publico ? "public, max-age=300" : "private, max-age=60";
        return Results.File(marca.Conteudo, marca.ContentType, entityTag: new EntityTagHeaderValue($"\"{marca.HashSha256}\""));
    }

    private static async Task<IResult> DefinirLogoPlataformaAsync(
        HttpContext contexto, IAntiforgery antiforgery, ServicoMarcaPlataforma servico, CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        var (bytes, tipo, erro) = await LerImagemAsync(contexto, cancellationToken).ConfigureAwait(false);
        if (erro is not null)
        {
            return Results.Redirect($"/admin/marca?erro={erro}");
        }

        try
        {
            await servico.DefinirAsync(bytes!, tipo!, cancellationToken).ConfigureAwait(false);
        }
        catch (EntradaInvalidaException)
        {
            return Results.Redirect("/admin/marca?erro=3");
        }
        catch
        {
            return Results.Redirect("/admin/marca?erro=4"); // autorização negada, etc. — sem vazar detalhe
        }

        return Results.Redirect("/admin/marca?ok=global");
    }

    private static async Task<IResult> DefinirLogoContribuinteAsync(
        HttpContext contexto, IAntiforgery antiforgery, IResolvedorTenant resolvedor, ServicoMarcaTenant servico, CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        var principal = contexto.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(contexto.User.FindFirstValue(ClaimsPortalBase.TenantSelecionado), out var tenantId))
        {
            return Results.Redirect("/admin/marca?erro=5");
        }

        try
        {
            await resolvedor.ResolverAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Results.Redirect("/admin/marca?erro=5");
        }

        var (bytes, tipo, erro) = await LerImagemAsync(contexto, cancellationToken).ConfigureAwait(false);
        if (erro is not null)
        {
            return Results.Redirect($"/admin/marca?erro={erro}");
        }

        try
        {
            await servico.DefinirAsync(bytes!, tipo!, cancellationToken).ConfigureAwait(false);
        }
        catch (EntradaInvalidaException)
        {
            return Results.Redirect("/admin/marca?erro=3");
        }
        catch
        {
            return Results.Redirect("/admin/marca?erro=4");
        }

        return Results.Redirect("/admin/marca?ok=contribuinte");
    }

    /// <summary>Lê o arquivo enviado (limitado), determina o tipo pelos MAGIC BYTES e valida o tamanho.</summary>
    private static async Task<(byte[]? Bytes, string? ContentType, int? Erro)> LerImagemAsync(HttpContext contexto, CancellationToken cancellationToken)
    {
        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var arquivo = formulario.Files[CampoArquivo];
        if (arquivo is null || arquivo.Length == 0)
        {
            return (null, null, 1); // ausente/vazio
        }

        if (arquivo.Length > MarcaVisual.TamanhoMaximoBytes)
        {
            return (null, null, 2); // excede o tamanho
        }

        using var memoria = new MemoryStream();
        await using (var origem = arquivo.OpenReadStream())
        {
            await origem.CopyToAsync(memoria, cancellationToken).ConfigureAwait(false);
        }

        var bytes = memoria.ToArray();
        var tipo = SnifarImagem(bytes);
        return tipo is null ? (null, null, 3) : (bytes, tipo, null); // 3 = formato não suportado
    }

    /// <summary>Identifica PNG/JPEG pelos bytes iniciais (fonte da verdade — nunca o content-type do cliente).</summary>
    private static string? SnifarImagem(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return null;
    }

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
