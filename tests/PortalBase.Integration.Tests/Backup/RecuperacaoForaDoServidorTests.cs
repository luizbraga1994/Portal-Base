using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PortalBase.Application.Backup;
using PortalBase.Infrastructure.Backup;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Backup;

/// <summary>
/// BKP-001 (auditoria de 08/10/2026) — recuperação FORA do servidor. Antes, as chaves ficavam só no cofre DPAPI da
/// máquina: perdido o servidor, nenhum backup decifrava. Provas:
/// <list type="bullet">
/// <item>o pacote exportado do cofre (formato v1, o mesmo gravado pelo <c>cofre-windows.ps1 -Acao Exportar</c>) abre
/// com a senha certa e recusa senha errada/arquivo alterado sem revelar nada;</item>
/// <item>com a chave do pacote, um artefato real (AES-256-GCM) decifra em qualquer máquina, sem banco;</item>
/// <item>o decifrador nunca sobrescreve e nunca deixa saída parcial (adulterado ⇒ nada no destino).</item>
/// </list>
/// </summary>
public sealed class RecuperacaoForaDoServidorTests : IDisposable
{
    private const string NomeChave = "portalbase-prod-chave-backup";
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "portalbase-dr-" + Guid.NewGuid().ToString("N"));

    public RecuperacaoForaDoServidorTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    /// <summary>Escritor do formato v1 conforme a especificação (espelha a função do cofre-windows.ps1).</summary>
    private static byte[] CriarPacote(IReadOnlyDictionary<string, string> segredos, string senha, int iteracoes = 100_000)
    {
        var texto = new StringBuilder("COFRE v1\n");
        foreach (var (nome, valor) in segredos)
        {
            texto.Append(nome).Append(':').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(valor))).Append('\n');
        }

        var sal = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(16);
        var chaves = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(senha), sal, iteracoes, HashAlgorithmName.SHA256, 64);
        using var aes = Aes.Create();
        aes.Key = chaves[..32];
        var cifrado = aes.EncryptCbc(Encoding.UTF8.GetBytes(texto.ToString()), iv, PaddingMode.PKCS7);

        using var corpo = new MemoryStream();
        corpo.Write("COFREPKG01"u8);
        var it = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(it, iteracoes);
        corpo.Write(it);
        corpo.Write(sal);
        corpo.Write(iv);
        corpo.Write(cifrado);
        var mac = HMACSHA256.HashData(chaves[32..], corpo.ToArray());
        corpo.Write(mac);
        return corpo.ToArray();
    }

    private static string NovaChaveBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private async Task<string> ArtefatoCifradoAsync(string chaveBase64, byte[] conteudo)
    {
        var segredos = new ProvedorSegredosFake();
        segredos.Valores[NomeChave] = chaveBase64;
        var cripto = new CriptografiaBackupAesGcm(segredos, new OpcoesBackup { ChaveCriptografiaRef = NomeChave });
        var caminho = Path.Combine(_pasta, "tenantcompleto-x.bak.enc");
        await using var destino = File.Create(caminho);
        await cripto.CriptografarAsync(new MemoryStream(conteudo), destino);
        return caminho;
    }

    // ---- Pacote do cofre ----

    [Fact]
    public void Pacote_Abre_Com_A_Senha_Certa_E_Devolve_Todos_Os_Segredos()
    {
        var segredos = new Dictionary<string, string>
        {
            [NomeChave] = NovaChaveBase64(),
            ["portalbase-prod-mysql-admin"] = "s3nh@:com:dois-pontos e acentuação",
        };

        var aberto = PacoteCofreExportado.Abrir(CriarPacote(segredos, "senha do pacote longa o bastante"), "senha do pacote longa o bastante");

        aberto.Should().BeEquivalentTo(segredos);
    }

    [Fact]
    public void Senha_Errada_Ou_Pacote_Alterado_Falham_Sem_Revelar_Nada()
    {
        var valor = NovaChaveBase64();
        var pacote = CriarPacote(new Dictionary<string, string> { [NomeChave] = valor }, "senha certa e longa");

        var errada = () => PacoteCofreExportado.Abrir(pacote, "senha errada");
        errada.Should().Throw<InvalidDataException>().Which.Message.Should().NotContain(valor);

        var alterado = (byte[])pacote.Clone();
        alterado[60] ^= 0x01; // um bit no texto cifrado
        var abrirAlterado = () => PacoteCofreExportado.Abrir(alterado, "senha certa e longa");
        abrirAlterado.Should().Throw<InvalidDataException>();

        var naoEPacote = () => PacoteCofreExportado.Abrir(Encoding.UTF8.GetBytes("COFRE1:qualquer coisa"), "x");
        naoEPacote.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Iteracoes_Abaixo_Do_Minimo_Sao_Recusadas()
    {
        var fraco = CriarPacote(new Dictionary<string, string> { [NomeChave] = NovaChaveBase64() }, "senha", iteracoes: 1000);

        var abrir = () => PacoteCofreExportado.Abrir(fraco, "senha");

        abrir.Should().Throw<InvalidDataException>("pacote com derivação fraca não é aceito (força bruta barata)");
    }

    [Fact]
    public async Task Provedor_Do_Pacote_So_Entrega_O_Que_Esta_Nele()
    {
        var provedor = new ProvedorSegredosPacote(new Dictionary<string, string> { [NomeChave] = "x" });

        (await provedor.ObterAsync(NomeChave.ToUpperInvariant())).Should().Be("x", "nomes do cofre são minúsculos");
        var ausente = async () => await provedor.ObterAsync("portalbase-prod-chave-mfa");
        await ausente.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Decifrar um artefato fora do portal ----

    [Fact]
    public async Task Com_A_Chave_Do_Pacote_Um_Dump_Real_Decifra_Sem_Banco()
    {
        var chave = NovaChaveBase64();
        var dump = Encoding.UTF8.GetBytes("-- MySQL dump 10.13\nCREATE TABLE t (v DECIMAL(18,2));\nINSERT INTO t VALUES (1234567.89);\n");
        var artefato = await ArtefatoCifradoAsync(chave, dump);
        var pacote = CriarPacote(new Dictionary<string, string> { [NomeChave] = chave }, "senha do pacote");
        var segredos = new ProvedorSegredosPacote(PacoteCofreExportado.Abrir(pacote, "senha do pacote"));
        var saida = Path.Combine(_pasta, "restaurar", "catalogo.sql");

        var r = await DecifradorArtefatoBackup.DecifrarAsync(segredos, NomeChave, artefato, saida);

        (await File.ReadAllBytesAsync(saida)).Should().Equal(dump);
        r.Conteudo.Should().Be(ConteudoArtefatoBackup.DumpSql);
        r.BytesDecifrados.Should().Be(dump.Length);
        r.HashArtefatoSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(artefato))),
            "o hash mostrado é o do ARTEFATO, para conferir com o registrado no catálogo");
    }

    [Theory] // o dump REAL do mysqldump (--skip-comments) começa com "\n/*!40101 SET ..." — achado no ensaio de 08/10/2026
    [InlineData("\n/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;\n")]
    [InlineData("-- MySQL dump 10.13\n")]
    [InlineData("\r\n\r\nCREATE TABLE `t` (`v` int);\n")]
    public async Task Dump_Real_E_Reconhecido_Como_Sql(string inicio)
    {
        var chave = NovaChaveBase64();
        var artefato = await ArtefatoCifradoAsync(chave, Encoding.UTF8.GetBytes(inicio));
        var segredos = new ProvedorSegredosFake();
        segredos.Valores[NomeChave] = chave;

        var r = await DecifradorArtefatoBackup.DecifrarAsync(segredos, NomeChave, artefato, Path.Combine(_pasta, Guid.NewGuid().ToString("N") + ".sql"));

        r.Conteudo.Should().Be(ConteudoArtefatoBackup.DumpSql);
    }

    [Fact]
    public async Task Pacote_De_Arquivos_E_Reconhecido_Como_Zip()
    {
        var chave = NovaChaveBase64();
        using var zip = new MemoryStream();
        using (var arquivo = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var e = arquivo.CreateEntry("arquivos/planilha.xlsx").Open();
            await e.WriteAsync("x"u8.ToArray());
        }

        var artefato = await ArtefatoCifradoAsync(chave, zip.ToArray());
        var segredos = new ProvedorSegredosFake();
        segredos.Valores[NomeChave] = chave;

        var r = await DecifradorArtefatoBackup.DecifrarAsync(segredos, NomeChave, artefato, Path.Combine(_pasta, "arquivos.zip"));

        r.Conteudo.Should().Be(ConteudoArtefatoBackup.PacoteZip);
    }

    [Fact]
    public async Task Nunca_Sobrescreve_A_Saida()
    {
        var chave = NovaChaveBase64();
        var artefato = await ArtefatoCifradoAsync(chave, "-- dump"u8.ToArray());
        var saida = Path.Combine(_pasta, "ja-existe.sql");
        await File.WriteAllTextAsync(saida, "NAO MEXER");
        var segredos = new ProvedorSegredosFake();
        segredos.Valores[NomeChave] = chave;

        var acao = async () => await DecifradorArtefatoBackup.DecifrarAsync(segredos, NomeChave, artefato, saida);

        await acao.Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(saida)).Should().Be("NAO MEXER");
    }

    [Fact]
    public async Task Artefato_Adulterado_Ou_Chave_Errada_Nao_Deixa_Nada_No_Destino()
    {
        var chave = NovaChaveBase64();
        // Dois blocos de 64 KiB + resto: o adulterado é o ÚLTIMO, depois de blocos válidos já decifrados.
        var conteudo = new byte[(64 * 1024 * 2) + 100];
        RandomNumberGenerator.Fill(conteudo);
        var artefato = await ArtefatoCifradoAsync(chave, conteudo);
        var bytes = await File.ReadAllBytesAsync(artefato);
        bytes[^10] ^= 0x01;
        await File.WriteAllBytesAsync(artefato, bytes);
        var segredos = new ProvedorSegredosFake();
        segredos.Valores[NomeChave] = chave;
        var saida = Path.Combine(_pasta, "saida.bin");

        var adulterado = async () => await DecifradorArtefatoBackup.DecifrarAsync(segredos, NomeChave, artefato, saida);
        await adulterado.Should().ThrowAsync<InvalidDataException>();
        File.Exists(saida).Should().BeFalse();
        Directory.GetFiles(_pasta, "*.parcial", SearchOption.AllDirectories).Should().BeEmpty();

        segredos.Valores[NomeChave] = NovaChaveBase64(); // outra chave
        var outroArtefato = await ArtefatoCifradoAsync(chave, "-- dump"u8.ToArray());
        var chaveErrada = async () => await DecifradorArtefatoBackup.DecifrarAsync(segredos, NomeChave, outroArtefato, saida);
        await chaveErrada.Should().ThrowAsync<InvalidDataException>();
        File.Exists(saida).Should().BeFalse();
    }
}
