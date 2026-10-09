using FluentAssertions;
using PortalBase.Application.Notificacoes;
using PortalBase.Infrastructure.Email;

namespace PortalBase.Integration.Tests.Email;

/// <summary>
/// Layout dos e-mails: tema do portal por padrão, botão de ação, valor em destaque, link do portal no rodapé (HTML e
/// texto), cor própria com texto do botão legível, e nada da configuração entra como HTML (rodapé codificado).
/// </summary>
public sealed class LayoutEmailTests
{
    private static OpcoesEmail Opcoes(string tema = "Portal", string cor = "", string rodape = "", string url = "https://portalbase.aconsultingbr.com/")
        => new()
        {
            UrlPublicaPortal = url,
            Remetente = new OpcoesRemetente { Email = "nao-responda@exemplo.com", Nome = "Portal Base" },
            Aparencia = new OpcoesAparenciaEmail { Tema = tema, CorDestaque = cor, TextoRodape = rodape },
        };

    private static readonly MensagemEmail Redefinicao = new(
        "maria@exemplo.com",
        "Redefinição de senha — Portal Base",
        "Olá, Maria.\n\nAbra o link: https://portalbase.aconsultingbr.com/conta/redefinir-senha?token=abc\n",
        "<p>Olá, Maria.</p><a data-acao href=\"https://portalbase.aconsultingbr.com/conta/redefinir-senha?token=abc\">Criar uma nova senha</a><p>O link vale por 30 minutos.</p>");

    [Fact]
    public void Padrao_Tem_O_Layout_Do_Portal_Botao_E_Link_Do_Portal()
    {
        var (texto, html) = LayoutEmail.Aplicar(Redefinicao, Opcoes());

        html.Should().StartWith("<!DOCTYPE html>").And.Contain("Portal Base").And.Contain("Andersen Consulting");
        html.Should().Contain("#0B1F3A", "cabeçalho azul-marinho do portal").And.Contain("bgcolor=\"#9E1B32\"", "botão em carmim");
        html.Should().Contain(">Redefini&#231;&#227;o de senha</h1>", "o título é o assunto sem o sufixo (acentos codificados)");
        html.Should().Contain("href=\"https://portalbase.aconsultingbr.com/conta/redefinir-senha?token=abc\">Criar uma nova senha</a>");
        html.Should().NotContain("data-acao");
        html.Should().Contain("href=\"https://portalbase.aconsultingbr.com\"").And.Contain("Acessar o Portal Base");
        texto.Should().Contain("Portal Base: https://portalbase.aconsultingbr.com").And.Contain("e-mail automático");
    }

    [Fact] // 08/10/2026: o botão "Criar minha senha" chegou com href relativo e o Outlook o mostrou quebrado
    public void Link_Relativo_Do_Conteudo_Sai_Absoluto_Com_O_Endereco_Do_Portal()
    {
        var mensagem = Redefinicao with
        {
            Html = "<p>Olá.</p><a data-acao href=\"/conta/redefinir-senha?token=abc&amp;x=1\">Criar minha senha</a>" +
                   "<p>Ou <a href=\"/conta/entrar\">entre</a>; veja também <a href=\"//cdn.exemplo.com/a\">outro site</a>.</p>",
        };

        var (_, html) = LayoutEmail.Aplicar(mensagem, Opcoes());

        html.Should().Contain("href=\"https://portalbase.aconsultingbr.com/conta/redefinir-senha?token=abc&amp;x=1\">Criar minha senha</a>");
        html.Should().Contain("href=\"https://portalbase.aconsultingbr.com/conta/entrar\">entre</a>");
        html.Should().Contain("href=\"//cdn.exemplo.com/a\"", "relativo ao protocolo não é caminho do portal");
        html.Should().NotContain("href=\"/conta");

        var (_, comCaminho) = LayoutEmail.Aplicar(mensagem, Opcoes(url: "https://exemplo.com.br/portal/"));
        comCaminho.Should().Contain("href=\"https://exemplo.com.br/portal/conta/entrar\"", "o portal publicado num caminho mantém o caminho");
    }

    [Fact]
    public void Botao_Tem_Espaco_Interno_Tambem_No_Outlook_Do_Windows()
    {
        var (_, html) = LayoutEmail.Aplicar(Redefinicao, Opcoes());

        // O Outlook ignora o padding do <a>; o da célula (mso-padding-alt) é o que ele usa. Os demais usam o do <a>.
        html.Should().Contain("mso-padding-alt:12px 26px\"><a style=\"display:inline-block;padding:12px 26px;");
    }

    [Fact]
    public void Destaque_Vira_Caixa_Monoespacada()
    {
        var mensagem = Redefinicao with { Html = "<p>Sua senha:</p><p data-destaque><strong>Ab#12345</strong></p>" };

        var (_, html) = LayoutEmail.Aplicar(mensagem, Opcoes());

        html.Should().NotContain("data-destaque").And.Contain("monospace").And.Contain("Ab#12345");
    }

    [Theory]
    [InlineData("PortalEscuro", "#C8364F")]
    [InlineData("Carmim", "#C0142A")]
    [InlineData("Azul", "#0B5CAD")]
    [InlineData("Neutro", "#222222")]
    [InlineData("Inexistente", "#9E1B32")]
    public void Cada_Tema_Usa_A_Sua_Cor_E_Desconhecido_Cai_No_Padrao(string tema, string corBotao)
    {
        var (_, html) = LayoutEmail.Aplicar(Redefinicao, Opcoes(tema));

        html.Should().Contain($"bgcolor=\"{corBotao}\"");
    }

    [Theory]
    [InlineData("#1A2B3C", "#FFFFFF")]
    [InlineData("#FFE600", "#111111")]
    public void Cor_Propria_No_Botao_Com_Texto_Legivel(string cor, string corTexto)
    {
        var (_, html) = LayoutEmail.Aplicar(Redefinicao, Opcoes(cor: cor));

        html.Should().Contain($"bgcolor=\"{cor}\"").And.Contain($"color:{corTexto};text-decoration:none;border-radius:8px");
    }

    [Fact]
    public void Rodape_E_Codificado_Nunca_Vira_Html()
    {
        var (texto, html) = LayoutEmail.Aplicar(Redefinicao, Opcoes(rodape: "Empresa <script>alert(1)</script>\nlinha 2"));

        html.Should().NotContain("<script>").And.Contain("Empresa &lt;script&gt;alert(1)&lt;/script&gt;<br>linha 2");
        texto.Should().Contain("Empresa <script>alert(1)</script>\nlinha 2", "no texto puro não há HTML a interpretar");
    }

    [Theory]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://portal")]
    public void Sem_Endereco_Http_Valido_Nao_Poe_Link_Do_Portal(string url)
    {
        var (texto, html) = LayoutEmail.Aplicar(Redefinicao, Opcoes(url: url));

        html.Should().NotContain("Acessar o Portal Base").And.NotContain("javascript:");
        texto.Should().NotContain("Portal Base: ");
    }

    [Fact]
    public void Sem_Html_Monta_A_Partir_Do_Texto_Codificado()
    {
        var mensagem = new MensagemEmail("maria@exemplo.com", "Aviso — Portal Base", "Linha <b>1</b>\n\nLinha 2");

        var (_, html) = LayoutEmail.Aplicar(mensagem, Opcoes());

        html.Should().Contain("Linha &lt;b&gt;1&lt;/b&gt;</p>").And.Contain("Linha 2</p>");
    }

    [Fact]
    public void Previa_E_So_O_Corpo_Com_O_Tema_Escolhido()
    {
        var previa = LayoutEmail.Previa(new OpcoesAparenciaEmail { Tema = "Azul" }, "https://portal.exemplo.com");

        previa.Should().StartWith("<table").And.NotContain("<html").And.Contain("#0B4F9C").And.Contain("Acessar o Portal Base");
    }
}
