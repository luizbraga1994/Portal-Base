using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PortalBase.Web.Seguranca;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Guarda de pendências da conta (servidor): com o MFA exigido e não cadastrado, ou com a senha temporária, nenhuma
/// página do portal é servida até a pendência ser resolvida — só a tela que a resolve, sair/entrar, os arquivos do site
/// e o canal do Blazor. Sem pendência, nada muda.
/// </summary>
public sealed class GuardaPendenciasContaTests
{
    private static ClaimsPrincipal Principal(params string?[] pendencias)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        foreach (var pendencia in pendencias)
        {
            if (pendencia is not null)
            {
                claims.Add(new Claim(ClaimsPortalBase.Pendencia, pendencia));
            }
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie"));
    }

    // SEC-001 (auditoria de 08/10/2026): com MFA cadastrado, a senha sozinha não dá acesso a nada além de informar o código
    // — nem à seleção do contribuinte, nem a endpoints.
    [Theory]
    [InlineData("/")]
    [InlineData("/conta/selecionar")]
    [InlineData("/conta/contribuinte")]
    [InlineData("/conta/minha-conta")]
    [InlineData("/conta/cadastrar-mfa")]
    [InlineData("/admin/usuarios")]
    [InlineData("/operacoes/backlog")]
    [InlineData("/arquivos/importar")]
    [InlineData("/admin/marca/plataforma")]
    [InlineData("/dados/conflitos/x.css")]
    public void Sem_O_Codigo_Mfa_Tudo_Leva_A_Verificacao(string caminho)
        => GuardaPendenciasConta.Destino(Principal(ClaimsPortalBase.PendenciaVerificacaoMfa), caminho).Should().Be("/conta/verificar");

    [Theory]
    [InlineData("/conta/verificar")]
    [InlineData("/conta/mfa")]
    [InlineData("/conta/logout")]
    [InlineData("/conta/sair")]
    [InlineData("/conta/entrar")]
    [InlineData("/conta/login")]
    [InlineData("/_blazor")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/css/portalbase.css")]
    [InlineData("/health")]
    public void Sem_O_Codigo_Mfa_So_A_Verificacao_E_Sair_Passam(string caminho)
        => GuardaPendenciasConta.Destino(Principal(ClaimsPortalBase.PendenciaVerificacaoMfa), caminho).Should().BeNull();

    [Theory]
    [InlineData("/conta/selecionar")]
    [InlineData("/conta/minha-conta")]
    [InlineData("/")]
    public void Codigo_Mfa_Vem_Antes_Da_Troca_Da_Senha_Temporaria(string caminho)
        => GuardaPendenciasConta.Destino(
                Principal(ClaimsPortalBase.PendenciaTrocaSenha, ClaimsPortalBase.PendenciaVerificacaoMfa), caminho)
            .Should().Be("/conta/verificar");

    [Theory]
    [InlineData("/")]
    [InlineData("/admin/usuarios")]
    [InlineData("/conta/selecionar")]
    [InlineData("/conta/minha-conta")]
    [InlineData("/conta/verificar")]
    [InlineData("/dados/conflitos/x.css")]
    [InlineData("/api/eventos")]
    public void Mfa_Exigido_E_Nao_Cadastrado_Leva_Ao_Cadastro(string caminho)
        => GuardaPendenciasConta.Destino(Principal(ClaimsPortalBase.PendenciaCadastroMfa), caminho).Should().Be("/conta/cadastrar-mfa");

    [Theory]
    [InlineData("/conta/cadastrar-mfa")]
    [InlineData("/conta/logout")]
    [InlineData("/conta/sair")]
    [InlineData("/conta/entrar")]
    [InlineData("/_blazor")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/css/portalbase.css")]
    [InlineData("/js/portalbase.js")]
    [InlineData("/fonts/archivo-latin-400-normal.woff2")]
    [InlineData("/app.css")]
    [InlineData("/PortalBase.Web.styles.css")]
    [InlineData("/health")]
    public void Durante_O_Cadastro_Do_Mfa_So_O_Necessario_Passa(string caminho)
        => GuardaPendenciasConta.Destino(Principal(ClaimsPortalBase.PendenciaCadastroMfa), caminho).Should().BeNull();

    [Theory]
    [InlineData("/")]
    [InlineData("/admin/usuarios")]
    [InlineData("/transmissao/lotes/abc.js")]
    [InlineData("/configuracoes/email")]
    public void Senha_Temporaria_Leva_A_Troca_Da_Senha(string caminho)
        => GuardaPendenciasConta.Destino(Principal(ClaimsPortalBase.PendenciaTrocaSenha), caminho).Should().Be("/conta/minha-conta?obrigatoria=1");

    [Theory]
    [InlineData("/conta/minha-conta")]
    [InlineData("/conta/verificar")]
    [InlineData("/conta/selecionar")]
    [InlineData("/conta/sair")]
    [InlineData("/css/portalbase.css")]
    public void Com_Senha_Temporaria_A_Area_Da_Conta_Continua_Acessivel(string caminho)
        => GuardaPendenciasConta.Destino(Principal(ClaimsPortalBase.PendenciaTrocaSenha), caminho).Should().BeNull();

    [Theory]
    [InlineData("/")]
    [InlineData("/admin/usuarios")]
    [InlineData("/conta/cadastrar-mfa")]
    [InlineData("/conta/selecionar")]
    public void Sem_Pendencia_Nada_Muda(string caminho)
        => GuardaPendenciasConta.Destino(Principal(), caminho).Should().BeNull();

    [Fact]
    public void Pendencia_Com_Valor_Desconhecido_Nao_Redireciona()
        => GuardaPendenciasConta.Destino(Principal("outra-coisa"), "/admin/usuarios").Should().BeNull();

    [Fact]
    public async Task Middleware_Redireciona_So_Quem_Esta_Autenticado_Com_Pendencia()
    {
        var chamouProximo = 0;
        var guarda = new GuardaPendenciasConta(_ => { chamouProximo++; return Task.CompletedTask; });

        var pendente = new DefaultHttpContext { User = Principal(ClaimsPortalBase.PendenciaCadastroMfa) };
        pendente.Request.Path = "/admin/usuarios";
        await guarda.InvokeAsync(pendente);
        pendente.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        pendente.Response.Headers.Location.ToString().Should().Be("/conta/cadastrar-mfa");
        chamouProximo.Should().Be(0);

        var anonimo = new DefaultHttpContext();
        anonimo.Request.Path = "/admin/usuarios";
        await guarda.InvokeAsync(anonimo);
        chamouProximo.Should().Be(1, "sem login, quem decide é a autenticação/autorização de sempre");
    }
}
