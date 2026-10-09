using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Infrastructure.Tempo;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Cofre local do Windows (<c>Vault:Modo=CofreWindows</c>) para servidor próprio/IIS: arquivo por segredo cifrado (DPAPI da
/// máquina no Windows; protetor de teste fora dele), nome validado (sem travessia de pasta), gravação atômica, fail-closed
/// para ausente/ilegível/de outra máquina; aceito em Produção só no Windows e com a pasta existente. A senha do catálogo
/// pode vir do cofre (connection string sem senha).
/// </summary>
public sealed class CofreWindowsTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "portalbase-cofre-" + Guid.NewGuid().ToString("N"));

    public CofreWindowsTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    [Fact]
    public async Task Grava_Le_E_Rotaciona_Sem_Deixar_Valor_Em_Claro()
    {
        var cofre = new ProvedorSegredosCofreWindows(_pasta, new ProtetorTeste());

        await cofre.DefinirAsync("PortalBase-Prod-Mysql-Runtime", "s3nh@-ÁÇ");
        (await cofre.ObterAsync("portalbase-prod-mysql-runtime")).Should().Be("s3nh@-ÁÇ", "o nome não diferencia maiúsculas");

        var arquivo = Path.Combine(_pasta, "portalbase-prod-mysql-runtime.segredo");
        var conteudo = await File.ReadAllTextAsync(arquivo);
        conteudo.Should().StartWith("COFRE1:").And.NotContain("s3nh@");

        await cofre.DefinirAsync("portalbase-prod-mysql-runtime", "nova");
        (await cofre.ObterAsync("portalbase-prod-mysql-runtime")).Should().Be("nova");
        Directory.GetFiles(_pasta).Should().ContainSingle("a troca é atômica e não sobra temporário");
    }

    [Theory]
    [InlineData("..\\fora")]
    [InlineData("../fora")]
    [InlineData("c:fora")]
    [InlineData("nome com espaço")]
    public async Task Nome_Invalido_E_Recusado(string nome)
    {
        var cofre = new ProvedorSegredosCofreWindows(_pasta, new ProtetorTeste());

        await cofre.Invoking(c => c.ObterAsync(nome).AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*inválido*");
        await cofre.Invoking(c => c.DefinirAsync(nome, "x").AsTask()).Should().ThrowAsync<InvalidOperationException>();
        Directory.GetFiles(_pasta).Should().BeEmpty();
    }

    [Fact]
    public async Task Ausente_Corrompido_Ou_De_Outra_Maquina_Falha_Fechado()
    {
        var cofre = new ProvedorSegredosCofreWindows(_pasta, new ProtetorTeste());
        await cofre.Invoking(c => c.ObterAsync("nao-existe").AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*não encontrado*");

        await File.WriteAllTextAsync(Path.Combine(_pasta, "corrompido.segredo"), "texto qualquer");
        await cofre.Invoking(c => c.ObterAsync("corrompido").AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*formato*");

        await cofre.DefinirAsync("outra-maquina", "valor");
        var outraMaquina = new ProvedorSegredosCofreWindows(_pasta, new ProtetorTeste(chave: 0x5A));
        await outraMaquina.Invoking(c => c.ObterAsync("outra-maquina").AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*outra máquina*");
    }

    [Fact]
    public async Task Arquivo_Trocado_De_Nome_Nao_Abre_Outro_Segredo()
    {
        var cofre = new ProvedorSegredosCofreWindows(_pasta, new ProtetorTeste());
        await cofre.DefinirAsync("chave-a", "valor-a");
        File.Copy(Path.Combine(_pasta, "chave-a.segredo"), Path.Combine(_pasta, "chave-b.segredo"));

        await cofre.Invoking(c => c.ObterAsync("chave-b").AsTask()).Should().ThrowAsync<InvalidOperationException>("a entropia amarra o conteúdo ao nome");
    }

    [Fact]
    public void Pasta_Inexistente_Falha_No_Arranque()
    {
        var acao = () => new ProvedorSegredosCofreWindows(Path.Combine(_pasta, "nao-existe"), new ProtetorTeste());
        acao.Should().Throw<InvalidOperationException>().WithMessage("*Preparar*");
    }

    [Fact]
    public void Producao_Com_Cofre_Windows_So_No_Windows_E_Com_Pasta()
    {
        static IConfiguration Config(params (string Chave, string? Valor)[] pares)
            => new ConfigurationBuilder().AddInMemoryCollection(pares.ToDictionary(p => p.Chave, p => p.Valor)).Build();

        var semPasta = () => new ServiceCollection().AddSegredos(Config(("Vault:Modo", "CofreWindows")), producao: true);
        semPasta.Should().Throw<InvalidOperationException>();

        var servicos = new ServiceCollection();
        servicos.AddSingleton<IRelogio, RelogioSistema>();
        var comPasta = () => servicos.AddSegredos(Config(("Vault:Modo", "CofreWindows"), ("Vault:DiretorioCofreWindows", _pasta)), producao: true);
        if (OperatingSystem.IsWindows())
        {
            comPasta.Should().NotThrow();
            using var provedor = servicos.BuildServiceProvider();
            provedor.GetRequiredService<IProvedorSegredos>().Should().BeOfType<ProvedorSegredosCache>();
        }
        else
        {
            comPasta.Should().Throw<InvalidOperationException>().WithMessage("*só funciona no Windows*");
        }
    }

    [SkippableFact]
    public async Task Dpapi_Da_Maquina_Faz_O_Ciclo_Completo_No_Windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI só existe no Windows.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cofre = new ProvedorSegredosCofreWindows(_pasta, new ProtetorDpapiMaquina());

        await cofre.DefinirAsync("portalbase-teste-dpapi", "valor-real");

        (await cofre.ObterAsync("portalbase-teste-dpapi")).Should().Be("valor-real");
    }

    /// <summary>
    /// Compatibilidade com <c>scripts/cofre-windows.ps1</c> (CI Windows): lê os segredos que o script gravou na pasta
    /// indicada por <c>PORTALBASE_TEST_COFRE_SCRIPT</c> — uma chave gerada (32 bytes em Base64) e um arquivo importado.
    /// </summary>
    [SkippableFact]
    public async Task Le_O_Que_O_Script_Do_Cofre_Gravou()
    {
        var pasta = Environment.GetEnvironmentVariable("PORTALBASE_TEST_COFRE_SCRIPT");
        Skip.If(!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(pasta), "Só no CI Windows, depois de rodar o script.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cofre = new ProvedorSegredosCofreWindows(pasta!, new ProtetorDpapiMaquina());

        Convert.FromBase64String(await cofre.ObterAsync("portalbase-ci-chave")).Should().HaveCount(32);
        Encoding.UTF8.GetString(Convert.FromBase64String(await cofre.ObterAsync("portalbase-ci-arquivo"))).Should().Be("conteudo do arquivo de teste");
    }

    [Fact]
    public void Senha_Do_Catalogo_Vem_Do_Cofre_E_A_Connection_String_Nao_Pode_Ter_Senha()
    {
        const string semSenha = "server=db;port=3306;database=portalbase_catalogo;user id=portalbase_catalogo";

        ConexaoCatalogo.Validar(semSenha, null);
        ConexaoCatalogo.Validar(semSenha, "portalbase-prod-mysql-catalogo");
        var ambigua = () => ConexaoCatalogo.Validar(semSenha + ";password=x", "portalbase-prod-mysql-catalogo");
        ambigua.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("password=x");

        ConexaoCatalogo.ComSenha(semSenha, "segredo").Should().Contain("segredo").And.Contain("portalbase_catalogo");
    }

    /// <summary>
    /// "Cifra" reversível só para testes fora do Windows: XOR com a entropia + marca da entropia (como o DPAPI, entropia
    /// diferente não abre); outro primeiro byte = outra máquina.
    /// </summary>
    private sealed class ProtetorTeste(byte chave = 0x33) : IProtetorCofreLocal
    {
        public byte[] Proteger(byte[] dados, byte[] entropia) => [chave, .. Marca(entropia), .. Misturar(dados, entropia)];

        public byte[] Desproteger(byte[] protegido, byte[] entropia)
        {
            if (protegido.Length < 5 || protegido[0] != chave || !protegido.AsSpan(1, 4).SequenceEqual(Marca(entropia)))
            {
                throw new CryptographicException("chave de outra máquina ou entropia diferente");
            }

            return Misturar(protegido[5..], entropia);
        }

        private static byte[] Marca(byte[] entropia) => SHA256.HashData(entropia)[..4];

        private static byte[] Misturar(byte[] dados, byte[] entropia)
        {
            var saida = new byte[dados.Length];
            for (var i = 0; i < dados.Length; i++)
            {
                saida[i] = (byte)(dados[i] ^ entropia[i % entropia.Length]);
            }

            return saida;
        }
    }
}
