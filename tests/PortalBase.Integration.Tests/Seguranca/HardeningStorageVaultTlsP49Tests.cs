using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using PortalBase.Application.Segredos;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Ingestao;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Prompt 49 — hardening consolidado de Storage/Vault/TLS. GUARDRAIL de código-fonte: falha se alguém
/// reintroduzir bypass de validação de certificado ou <c>SslMode</c> inseguro (regressão de TLS). Mais:
/// segredo isolado por tenant+ambiente, <c>SslMode</c> padrão seguro e o storage rejeitando path traversal.
/// Providers EXTERNOS reais (Key Vault, object storage, MySQL/RabbitMQ TLS) são gated (BLOCKED sem infra).
/// </summary>
public sealed class HardeningStorageVaultTlsP49Tests
{
    // ---- GUARDRAIL de código-fonte (Fases 16, 31, 34): trava a postura de TLS contra regressões ----

    [Fact] // NUNCA pode existir bypass de validação de certificado remoto no código
    public void Fonte_Sem_Bypass_De_Validacao_De_Certificado()
    {
        var ofensores = new List<string>();
        foreach (var arquivo in ArquivosFonte())
        {
            var texto = File.ReadAllText(arquivo);
            var temCallback = texto.Contains("ServerCertificateCustomValidationCallback", StringComparison.Ordinal)
                || texto.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal);
            var aceitaQualquer = texto.Contains("DangerousAcceptAnyServerCertificate", StringComparison.Ordinal)
                || (temCallback && texto.Contains("=> true", StringComparison.Ordinal))
                || texto.Contains("CheckCertificateRevocation = false", StringComparison.Ordinal);
            if (aceitaQualquer)
            {
                ofensores.Add(arquivo);
            }
        }

        ofensores.Should().BeEmpty("nenhum código pode desabilitar a validação do certificado TLS remoto (§16/§31)");
    }

    [Fact] // NUNCA pode existir SslMode inseguro (Preferred/None/Disabled) FIXADO no código
    public void Fonte_Sem_SslMode_Inseguro_Fixado()
    {
        var ofensores = new List<string>();
        foreach (var arquivo in ArquivosFonte())
        {
            var texto = File.ReadAllText(arquivo);
            if (texto.Contains("MySqlSslMode.Preferred", StringComparison.Ordinal)
                || texto.Contains("MySqlSslMode.None", StringComparison.Ordinal)
                || texto.Contains("MySqlSslMode.Disabled", StringComparison.Ordinal))
            {
                ofensores.Add(arquivo);
            }
        }

        ofensores.Should().BeEmpty("o SslMode inseguro nunca é fixado no código (só Required+ por padrão — §17/§48)");
    }

    [Fact] // §17/§48 — o padrão do banco de tenant exige TLS
    public void SslMode_Padrao_Exige_Tls()
        => new OpcoesBancoTenant().SslMode.Should().Be(MySqlSslMode.Required);

    // ---- Isolamento de SEGREDOS por tenant e por ambiente (Fases 11, 23, 32, 36) ----

    [Fact]
    public void Segredo_Isolado_Por_Tenant_E_Por_Ambiente()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        NomeSegredo.SegredoIntegracao("prod", a).Should().NotBe(NomeSegredo.SegredoIntegracao("prod", b));            // tenant A ≠ B
        NomeSegredo.SegredoIntegracao("prod", a).Should().NotBe(NomeSegredo.SegredoIntegracao("homolog", a));         // ambiente ≠
        NomeSegredo.SegredoIntegracao("prod", a).Should().NotBe(NomeSegredo.AuditoriaHmac("prod"));                   // tenant ≠ plataforma
    }

    // ---- Storage: path traversal / caminho hostil rejeitado (Fases 3, 4) sobre filesystem REAL (temp) ----

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/../../x.bin")]
    [InlineData("a\\..\\..\\b")]
    [InlineData("\\\\servidor\\share\\x")] // UNC
    [InlineData("")]
    public async Task Storage_Rejeita_Caminho_Hostil(string caminhoHostil)
    {
        var raiz = Path.Combine(Path.GetTempPath(), "portalbase-p49-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ArmazenamentoArquivosFilesystem(Options.Create(new OpcoesArmazenamento { DiretorioRaiz = raiz }));
            var acao = async () => await storage.SalvarAsync(caminhoHostil, new MemoryStream(Encoding.UTF8.GetBytes("x")), CancellationToken.None);
            await acao.Should().ThrowAsync<EntradaInvalidaException>();
        }
        finally
        {
            if (Directory.Exists(raiz))
            {
                Directory.Delete(raiz, recursive: true);
            }
        }
    }

    [Fact] // caminho legítimo (gerado pelo servidor) é aceito e fica DENTRO da raiz
    public async Task Storage_Aceita_Caminho_Seguro_Dentro_Da_Raiz()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "portalbase-p49-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ArmazenamentoArquivosFilesystem(Options.Create(new OpcoesArmazenamento { DiretorioRaiz = raiz }));
            var referencia = await storage.SalvarAsync("tenant_a/" + Guid.NewGuid().ToString("N") + ".bin",
                new MemoryStream(Encoding.UTF8.GetBytes("A-ONLY")), CancellationToken.None);

            referencia.Should().StartWith("tenant_a/");
            await using var leitura = await storage.AbrirLeituraAsync(referencia);
            using var sr = new StreamReader(leitura);
            (await sr.ReadToEndAsync()).Should().Be("A-ONLY");
        }
        finally
        {
            if (Directory.Exists(raiz))
            {
                Directory.Delete(raiz, recursive: true);
            }
        }
    }

    private static IEnumerable<string> ArquivosFonte()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("a raiz do repositório (PortalBase.sln) deve ser localizável para o guardrail de fonte");
        var src = Path.Combine(dir!.FullName, "src");
        Directory.Exists(src).Should().BeTrue();
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
