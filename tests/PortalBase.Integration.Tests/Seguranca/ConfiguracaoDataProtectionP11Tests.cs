using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Web.Seguranca;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Endurecimento do chaveiro de Data Protection (Prompt 11 — segurança operacional). O chaveiro
/// protege o cookie de autenticação e o token antiforgery. Provamos: (1) nome de aplicação ESTÁVEL
/// (mesmo discriminador entre instâncias, para o chaveiro ser compartilhado entre réplicas);
/// (2) sem configuração ⇒ padrão do framework, protetor funcional (round-trip); (3) com
/// <c>DataProtection:CaminhoChaves</c> ⇒ o chaveiro é PERSISTIDO no volume; (4) caminho inutilizável
/// ⇒ FALHA no startup (nunca degrada em silêncio para um chaveiro efêmero em produção).
/// </summary>
public sealed class ConfiguracaoDataProtectionP11Tests
{
    private static IConfiguration Config(params (string Chave, string? Valor)[] pares)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pares.ToDictionary(p => p.Chave, p => p.Valor))
            .Build();

    private static ServiceProvider Compor(IConfiguration configuracao)
        => new ServiceCollection()
            .AddDataProtectionEndurecido(configuracao)
            .BuildServiceProvider();

    [Fact]
    public void Sem_Configuracao_Protetor_Funciona_E_RoundTrip()
    {
        using var provedor = Compor(Config());

        var protetor = provedor.GetRequiredService<IDataProtectionProvider>().CreateProtector("teste");
        var claro = "cookie-de-sessao";

        var protegido = protetor.Protect(claro);
        protegido.Should().NotBe(claro);
        protetor.Unprotect(protegido).Should().Be(claro);
    }

    [Fact]
    public void Nome_De_Aplicacao_E_Estavel_Entre_Instancias()
    {
        // Duas composições independentes com o MESMO caminho persistente devem interoperar:
        // o discriminador de propósito (SetApplicationName) precisa ser idêntico, senão o Unprotect falha.
        var dir = DiretorioTemporario();
        try
        {
            var cfg = Config((ConfiguracaoDataProtection.ChaveCaminho, dir));
            using var provedorA = (ServiceProvider)Compor(cfg);
            using var provedorB = (ServiceProvider)Compor(cfg);

            var protegido = provedorA.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("sessao").Protect("valor-x");
            var recuperado = provedorB.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("sessao").Unprotect(protegido);

            recuperado.Should().Be("valor-x");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Com_Caminho_Configurado_Persiste_Chaveiro_No_Volume()
    {
        var dir = DiretorioTemporario();
        try
        {
            using var provedor = (ServiceProvider)Compor(Config((ConfiguracaoDataProtection.ChaveCaminho, dir)));

            // Forçar a geração do chaveiro exercitando o protetor.
            provedor.GetRequiredService<IDataProtectionProvider>().CreateProtector("sessao").Protect("v");

            Directory.GetFiles(dir, "key-*.xml").Should().NotBeEmpty(
                "o chaveiro deve ser persistido no diretório configurado (não efêmero)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Caminho_Inutilizavel_Falha_Fast_No_Startup()
    {
        // Um arquivo (não diretório) no lugar do caminho torna a criação/escrita impossível.
        var arquivo = Path.Combine(Path.GetTempPath(), $"dp-arquivo-{Guid.NewGuid():N}");
        File.WriteAllText(arquivo, "ocupado");
        try
        {
            var acao = () => Compor(Config((ConfiguracaoDataProtection.ChaveCaminho, arquivo)));
            acao.Should().Throw<InvalidOperationException>()
                .WithMessage("*Data Protection*");
        }
        finally
        {
            File.Delete(arquivo);
        }
    }

    private static string DiretorioTemporario()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dp-chaves-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
