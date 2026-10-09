using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Diagnostics;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Respostas de erro de requisições que NÃO são GET/HEAD (POST de formulário: login, MFA, sair, envio de arquivo...).
/// O re-execute das páginas de status (<c>UseStatusCodePagesWithReExecute</c>) repetiria o POST contra a página Blazor
/// <c>/not-found</c>, que recusa POST sem antiforgery — e o 429 do limite de tentativas chegava ao usuário como 400 sem
/// explicação (OBS-P62R1-01). Aqui o re-execute fica desligado para esses métodos e, se a resposta de erro sair sem
/// corpo, devolvemos o código REAL (429, 400, 403...) com uma página curta em português. Nunca detalhe interno (regra 16).
/// </summary>
public sealed class RespostaErroFormulario
{
    // Codifica o HTML sem transformar acentos em entidades (textos fixos em português).
    private static readonly HtmlEncoder Codificador = HtmlEncoder.Create(UnicodeRanges.All);

    private readonly RequestDelegate _proximo;

    public RespostaErroFormulario(RequestDelegate proximo)
    {
        ArgumentNullException.ThrowIfNull(proximo);
        _proximo = proximo;
    }

    public async Task InvokeAsync(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        if (HttpMethods.IsGet(contexto.Request.Method) || HttpMethods.IsHead(contexto.Request.Method))
        {
            await _proximo(contexto).ConfigureAwait(false);
            return;
        }

        if (contexto.Features.Get<IStatusCodePagesFeature>() is { } paginasDeStatus)
        {
            paginasDeStatus.Enabled = false;
        }

        await _proximo(contexto).ConfigureAwait(false);

        var resposta = contexto.Response;
        if (resposta.StatusCode is < 400 or >= 600 || resposta.HasStarted || resposta.ContentLength is not null
            || !string.IsNullOrEmpty(resposta.ContentType))
        {
            return;
        }

        var (titulo, mensagem) = Mensagem(resposta.StatusCode);
        var voltar = contexto.Request.Path.StartsWithSegments("/conta", StringComparison.OrdinalIgnoreCase) ? "/conta/entrar" : "/";
        resposta.ContentType = "text/html; charset=utf-8";
        await resposta.WriteAsync(Html(titulo, mensagem, voltar), contexto.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Título e texto para o código de status (exposto para teste).</summary>
    public static (string Titulo, string Mensagem) Mensagem(int status) => status switch
    {
        StatusCodes.Status429TooManyRequests => ("Muitas tentativas",
            "Foram feitas muitas tentativas em pouco tempo a partir desta rede. Aguarde alguns minutos (até 5) e tente de novo."),
        StatusCodes.Status400BadRequest => ("Solicitação inválida",
            "O formulário expirou ou não pôde ser validado. Volte à página, recarregue-a e tente de novo."),
        StatusCodes.Status403Forbidden => ("Sem permissão",
            "Seu perfil não tem permissão para esta operação. Se precisar do acesso, fale com o administrador."),
        StatusCodes.Status404NotFound => ("Página não encontrada",
            "O endereço que você abriu não existe (ou não está mais disponível). Confira o endereço ou volte ao início."),
        StatusCodes.Status413PayloadTooLarge => ("Arquivo grande demais",
            "O envio passou do tamanho máximo aceito pelo portal. Reduza o arquivo e tente de novo."),
        _ => ("Não foi possível concluir",
            "Ocorreu um erro ao processar a sua solicitação. Tente de novo em instantes."),
    };

    private static string Html(string titulo, string mensagem, string voltar)
        => "<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\" />"
           + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />"
           + $"<title>{Codificador.Encode(titulo)} · Portal Base</title>"
           + "<link rel=\"stylesheet\" href=\"/css/portalbase.css\" /></head><body>"
           + "<div class=\"ground-center\"><div class=\"w-full mw-400\"><div class=\"card card-pad\">"
           + $"<h1 class=\"auth__title\">{Codificador.Encode(titulo)}</h1>"
           + $"<p class=\"auth__subtitle\" id=\"mensagem-status\">{Codificador.Encode(mensagem)}</p>"
           + $"<a class=\"btn btn-primary w-full jc-center mt-4\" href=\"{voltar}\">Voltar</a>"
           + "</div></div></div></body></html>";
}
