using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PortalBase.Application.Notificacoes;
using PortalBase.Domain.Notificacoes;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Layout dos e-mails do portal: cabeçalho com a marca, cartão com o conteúdo, botão de ação e rodapé com o LINK DO
/// PORTAL, nas cores do tema escolhido em Configurações → E-mail (<see cref="TemasEmail"/>). HTML de e-mail clássico —
/// tabelas e estilos inline — para sair igual no Outlook, Gmail e celular.
/// O conteúdo vem dos casos de uso como um fragmento simples (já codificado por eles): parágrafos <c>&lt;p&gt;</c>,
/// um botão marcado como <c>&lt;a data-acao href="..."&gt;</c> e um valor em destaque <c>&lt;p data-destaque&gt;</c>
/// (ex.: a senha temporária). Os textos da configuração (rodapé, URL) são codificados aqui (sem injeção de HTML).
/// Link relativo do conteúdo ("/conta/...") sai ABSOLUTO com o endereço do portal vigente no envio: o leitor de e-mail
/// não tem como abrir um caminho sem o site (o Outlook mostra o botão quebrado, "[/conta/...]Texto").
/// </summary>
public static partial class LayoutEmail
{
    private const string Fonte = "'Segoe UI',Arial,Helvetica,sans-serif";
    private const string FonteMono = "Consolas,'Courier New',monospace";
    private const string SufixoAssunto = " — Portal Base";

    private sealed record Paleta(
        string Fundo, string Cabecalho, string CabecalhoTexto, string CabecalhoSub, string? Faixa, string Cartao,
        string Texto, string Secundario, string Destaque, string Borda, string DestaqueFundo, string Link);

    private static readonly Dictionary<string, Paleta> Paletas = new(StringComparer.Ordinal)
    {
        ["Portal"] = new("#EEF2F7", "#0B1F3A", "#FFFFFF", "#AEB9CC", "#9E1B32", "#FFFFFF", "#141821", "#4F5868", "#9E1B32", "#DDE3EB", "#F6F8FB", "#264B78"),
        ["PortalEscuro"] = new("#0A1424", "#0B1F3A", "#FFFFFF", "#93A1B8", "#C8364F", "#111D33", "#E6EBF5", "#A7B2C6", "#C8364F", "#22324D", "#0D1830", "#8FB4E8"),
        ["Carmim"] = new("#F3F4F5", "#FFFFFF", "#C0142A", "#6B6B6B", "#C0142A", "#FFFFFF", "#333333", "#5F5F5F", "#C0142A", "#D5D6D8", "#F7F7F8", "#C0142A"),
        ["Azul"] = new("#EEF3FB", "#0B4F9C", "#FFFFFF", "#C9DAF2", null, "#FFFFFF", "#1B2635", "#52627A", "#0B5CAD", "#D6E0EE", "#F3F7FD", "#0B5CAD"),
        ["Neutro"] = new("#F5F5F5", "#FFFFFF", "#111111", "#666666", "#111111", "#FFFFFF", "#222222", "#555555", "#222222", "#E0E0E0", "#F7F7F7", "#222222"),
    };

    /// <summary>Texto e HTML finais da mensagem, com o layout, o tema e o rodapé (link do portal) aplicados.</summary>
    public static (string Texto, string Html) Aplicar(MensagemEmail mensagem, OpcoesEmail opcoes)
    {
        ArgumentNullException.ThrowIfNull(mensagem);
        ArgumentNullException.ThrowIfNull(opcoes);
        var aparencia = opcoes.Aparencia ?? new OpcoesAparenciaEmail();
        var fragmento = string.IsNullOrEmpty(mensagem.Html) ? FragmentoDoTexto(mensagem.Texto) : mensagem.Html;
        var corpo = MontarCorpo(Titulo(mensagem.Assunto), fragmento, aparencia, opcoes.UrlPublicaPortal);
        var html = new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(WebUtility.HtmlEncode(mensagem.Assunto)).Append("</title></head>")
            .Append(CultureInfo.InvariantCulture, $"<body style=\"margin:0;padding:0;background:{Cores(aparencia).Fundo}\">")
            .Append(corpo)
            .Append("</body></html>")
            .ToString();
        return (TextoComRodape(mensagem.Texto, aparencia, opcoes.UrlPublicaPortal), html);
    }

    /// <summary>
    /// Pré-visualização para a tela (só o corpo, sem &lt;html&gt;): um e-mail de exemplo com o tema, a cor e o rodapé
    /// informados. Valores inválidos caem no padrão (a tela valida antes de salvar).
    /// </summary>
    public static string Previa(OpcoesAparenciaEmail aparencia, string? urlPublicaPortal)
    {
        ArgumentNullException.ThrowIfNull(aparencia);
        const string exemplo =
            "<p>Olá, Maria Souza.</p>" +
            "<p>Um administrador do <strong>Portal Base</strong> enviou a você um link para criar uma nova senha.</p>" +
            "<a data-acao href=\"#\">Criar uma nova senha</a>" +
            "<p>O link vale por 30 minutos e só pode ser usado uma vez.</p>";
        return MontarCorpo("Redefinição de senha", exemplo, aparencia, urlPublicaPortal);
    }

    private static string MontarCorpo(string titulo, string fragmento, OpcoesAparenciaEmail aparencia, string? urlPublicaPortal)
    {
        var p = Cores(aparencia);
        var botaoTexto = TextoSobre(p.Destaque);
        var url = (urlPublicaPortal ?? string.Empty).Trim().TrimEnd('/');
        var urlValida = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        var conteudo = EstilizarFragmento(urlValida ? LinksAbsolutos(fragmento, url) : fragmento, p, botaoTexto);

        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture,
            $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"{p.Fundo}\" style=\"background:{p.Fundo}\"><tr><td align=\"center\" style=\"padding:24px 12px\">");
        html.Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;max-width:600px\">");
        if (p.Faixa is not null && p.Cabecalho == "#FFFFFF")
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td height=\"5\" bgcolor=\"{p.Faixa}\" style=\"background:{p.Faixa};font-size:0;line-height:0;border-radius:12px 12px 0 0\">&nbsp;</td></tr>");
        }

        var bordaCabecalho = p.Cabecalho == "#FFFFFF" ? $"border:1px solid {p.Borda};border-bottom:0;" : string.Empty;
        var cantos = p.Faixa is not null && p.Cabecalho == "#FFFFFF" ? string.Empty : "border-radius:12px 12px 0 0;";
        html.Append(CultureInfo.InvariantCulture,
            $"<tr><td bgcolor=\"{p.Cabecalho}\" style=\"background:{p.Cabecalho};padding:20px 28px;{cantos}{bordaCabecalho}\">");
        html.Append(CultureInfo.InvariantCulture,
            $"<div style=\"font-family:{Fonte};font-size:20px;font-weight:700;color:{p.CabecalhoTexto}\">Portal Base</div>");
        html.Append(CultureInfo.InvariantCulture,
            $"<div style=\"font-family:{Fonte};font-size:11px;letter-spacing:2px;text-transform:uppercase;color:{p.CabecalhoSub};margin-top:2px\">Andersen Consulting</div>");
        html.Append("</td></tr>");
        if (p.Faixa is not null && p.Cabecalho != "#FFFFFF")
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td height=\"3\" bgcolor=\"{p.Faixa}\" style=\"background:{p.Faixa};font-size:0;line-height:0\">&nbsp;</td></tr>");
        }

        html.Append(CultureInfo.InvariantCulture,
            $"<tr><td bgcolor=\"{p.Cartao}\" style=\"background:{p.Cartao};padding:28px;border:1px solid {p.Borda};border-top:0;border-radius:0 0 12px 12px;font-family:{Fonte}\">");
        html.Append(CultureInfo.InvariantCulture,
            $"<h1 style=\"margin:0 0 18px;font-family:{Fonte};font-size:19px;font-weight:700;color:{p.Texto}\">{WebUtility.HtmlEncode(titulo)}</h1>");
        html.Append(conteudo);
        html.Append("</td></tr>");

        html.Append(CultureInfo.InvariantCulture,
            $"<tr><td align=\"center\" style=\"padding:18px 28px;font-family:{Fonte};font-size:12px;line-height:1.6;color:{p.Secundario}\">");
        if (urlValida)
        {
            var urlHtml = WebUtility.HtmlEncode(url);
            html.Append(CultureInfo.InvariantCulture,
                $"<a href=\"{urlHtml}\" style=\"color:{p.Link};font-weight:700;text-decoration:none\">Acessar o Portal Base</a><br>");
            html.Append(CultureInfo.InvariantCulture,
                $"<a href=\"{urlHtml}\" style=\"color:{p.Secundario};text-decoration:none\">{WebUtility.HtmlEncode(uri!.Host + uri.AbsolutePath.TrimEnd('/'))}</a><br>");
        }

        var rodape = (aparencia.TextoRodape ?? string.Empty).Trim();
        if (rodape.Length > 0)
        {
            html.Append(WebUtility.HtmlEncode(rodape).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal)).Append("<br>");
        }

        html.Append("Este é um e-mail automático do Portal Base. Não responda a esta mensagem.");
        html.Append("</td></tr></table></td></tr></table>");
        return html.ToString();
    }

    private static string TextoComRodape(string texto, OpcoesAparenciaEmail aparencia, string? urlPublicaPortal)
    {
        var final = new StringBuilder((texto ?? string.Empty).TrimEnd()).Append("\n\n--\n");
        var url = (urlPublicaPortal ?? string.Empty).Trim().TrimEnd('/');
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            final.Append("Portal Base: ").Append(url).Append('\n');
        }

        var rodape = (aparencia.TextoRodape ?? string.Empty).Trim();
        if (rodape.Length > 0)
        {
            final.Append(rodape.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
        }

        return final.Append("Este é um e-mail automático do Portal Base. Não responda a esta mensagem.\n").ToString();
    }

    private static string EstilizarFragmento(string fragmento, Paleta p, string botaoTexto)
    {
        var resultado = Acao().Replace(fragmento, m =>
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:22px 0\"><tr>" +
            // mso-padding-alt: o Outlook do Windows ignora o padding do <a>; sem isto, o texto encosta na borda do botão.
            $"<td bgcolor=\"{p.Destaque}\" style=\"background:{p.Destaque};border-radius:8px;mso-padding-alt:12px 26px\">" +
            // style ANTES do href: a troca dos links comuns logo abaixo ("<a href=") não pode alcançar o botão.
            $"<a style=\"display:inline-block;padding:12px 26px;font-family:{Fonte};font-size:15px;font-weight:700;color:{botaoTexto};text-decoration:none;border-radius:8px\" href=\"{m.Groups["href"].Value}\">{m.Groups["texto"].Value}</a>" +
            "</td></tr></table>");
        resultado = resultado.Replace("<p data-destaque>",
            $"<p style=\"margin:16px 0;padding:14px 16px;background:{p.DestaqueFundo};border:1px solid {p.Borda};border-radius:8px;font-family:{FonteMono};font-size:18px;letter-spacing:1px;text-align:center;color:{p.Texto}\">",
            StringComparison.Ordinal);
        resultado = resultado.Replace("<p>",
            $"<p style=\"margin:0 0 14px;font-family:{Fonte};font-size:15px;line-height:1.6;color:{p.Texto}\">", StringComparison.Ordinal);
        resultado = resultado.Replace("<a href=", $"<a style=\"color:{p.Link};text-decoration:underline\" href=", StringComparison.Ordinal);
        return resultado;
    }

    /// <summary>
    /// <c>href="/..."</c> (caminho do portal) ⇒ <c>href="https://portal/..."</c>. O endereço é o da configuração do
    /// servidor (já validado http/https); "//host" (relativo ao protocolo) não é caminho do portal e fica como está.
    /// </summary>
    private static string LinksAbsolutos(string fragmento, string urlPortal)
    {
        var baseHtml = WebUtility.HtmlEncode(urlPortal);
        return HrefRelativo().Replace(fragmento, m => $"{m.Groups["inicio"].Value}{baseHtml}/");
    }

    private static Paleta Cores(OpcoesAparenciaEmail aparencia)
    {
        var paleta = Paletas[TemasEmail.Obter(aparencia.Tema).Codigo];
        var cor = aparencia.CorDestaque?.Trim();
        return ConfiguracaoEmail.CorValida(cor)
            ? paleta with { Destaque = cor!.ToUpperInvariant(), Link = cor!.ToUpperInvariant(), Faixa = paleta.Faixa is null ? null : cor!.ToUpperInvariant() }
            : paleta;
    }

    /// <summary>Branco ou quase-preto, o que tiver mais contraste sobre a cor (WCAG) — vale para a cor personalizada.</summary>
    private static string TextoSobre(string corHex)
    {
        double Canal(int inicio)
        {
            var c = int.Parse(corHex.AsSpan(inicio, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var luminancia = (0.2126 * Canal(1)) + (0.7152 * Canal(3)) + (0.0722 * Canal(5));
        var contrasteBranco = 1.05 / (luminancia + 0.05);
        var contrastePreto = (luminancia + 0.05) / 0.05;
        return contrasteBranco >= contrastePreto ? "#FFFFFF" : "#111111";
    }

    private static string Titulo(string assunto)
        => assunto.EndsWith(SufixoAssunto, StringComparison.Ordinal) ? assunto[..^SufixoAssunto.Length] : assunto;

    private static string FragmentoDoTexto(string texto)
        => string.Concat((texto ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => "<p>" + WebUtility.HtmlEncode(p).Replace("\n", "<br>", StringComparison.Ordinal) + "</p>"));

    [GeneratedRegex("(?<inicio><a\\b[^>]*\\bhref=\")/(?!/)", RegexOptions.CultureInvariant)]
    private static partial Regex HrefRelativo();

    [GeneratedRegex("(?:<p>\\s*)?<a data-acao href=\"(?<href>[^\"]*)\">(?<texto>.*?)</a>(?:\\s*</p>)?", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Acao();
}
