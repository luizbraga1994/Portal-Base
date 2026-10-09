using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using PortalBase.Application.Backup;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Backup;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Backup;

/// <summary>
/// Prompt 50 — STORAGE de backup e CIFRA em repouso sobre implementações REAIS (filesystem temporário +
/// AES-256-GCM com chave do "cofre" fake). Prova: roundtrip preserva o conteúdo e o hash (Fase 23); path
/// traversal rejeitado (Fase 22); cifra volta ao original; chave errada / artefato adulterado FALHAM
/// (Fases 24/26) — a base para a detecção de corrupção. Backup/restore FÍSICO de MySQL fica gated/BLOCKED.
/// </summary>
public sealed class BackupStorageCryptoP50Tests
{
    // ---- Store de artefatos (filesystem real, temporário) ----

    [Fact]
    public async Task Store_Backup_Roundtrip_Preserva_Conteudo_E_Hash()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "portalbase-bkp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArmazenamentoBackupFilesystem(Options.Create(new OpcoesArmazenamentoBackup { DiretorioRaiz = raiz }));
            var original = Encoding.UTF8.GetBytes("BACKUP-A-CONTEUDO");
            var referencia = "tenantcompleto/aaaa/20260916-x.bak.enc";

            var local = await store.GravarAsync(referencia, new MemoryStream(original), CancellationToken.None);
            local.Should().Be(referencia);

            await using var leitura = await store.AbrirAsync(referencia);
            using var ms = new MemoryStream();
            await leitura.CopyToAsync(ms);

            ms.ToArray().Should().Equal(original);
            SHA256.HashData(ms.ToArray()).Should().Equal(SHA256.HashData(original)); // Fase 23: hash igual
        }
        finally
        {
            if (Directory.Exists(raiz))
            {
                Directory.Delete(raiz, recursive: true);
            }
        }
    }

    [Theory] // Fase 22 — referência hostil rejeitada (não escapa a raiz do store)
    [InlineData("../fora.bak")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b.bak")]
    [InlineData("")]
    public async Task Store_Rejeita_Referencia_Hostil(string referenciaHostil)
    {
        var raiz = Path.Combine(Path.GetTempPath(), "portalbase-bkp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArmazenamentoBackupFilesystem(Options.Create(new OpcoesArmazenamentoBackup { DiretorioRaiz = raiz }));
            var acao = async () => await store.GravarAsync(referenciaHostil, new MemoryStream([1, 2, 3]), CancellationToken.None);
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

    // ---- Cifra AES-256-GCM em repouso (chave do cofre fake) ----

    private static (CriptografiaBackupAesGcm Cripto, ProvedorSegredosFake Segredos) Criar(string chaveRef)
    {
        var segredos = new ProvedorSegredosFake();
        segredos.Valores[chaveRef] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var cripto = new CriptografiaBackupAesGcm(segredos, new OpcoesBackup { ChaveCriptografiaRef = chaveRef });
        return (cripto, segredos);
    }

    [Fact]
    public async Task Cripto_Roundtrip_Volta_Ao_Original()
    {
        var (cripto, _) = Criar("backup-key");
        var original = RandomNumberGenerator.GetBytes(200_000); // > 1 bloco (força o framing por blocos)

        using var cifrado = new MemoryStream();
        await cripto.CriptografarAsync(new MemoryStream(original), cifrado);
        cifrado.ToArray().Should().NotEqual(original); // realmente cifrou

        cifrado.Position = 0;
        using var claro = new MemoryStream();
        await cripto.DecriptografarAsync(cifrado, claro);
        claro.ToArray().Should().Equal(original);
    }

    [Fact] // Fase 24/26 — chave errada não decifra (isolamento de chave)
    public async Task Cripto_Chave_Errada_Falha()
    {
        var (enc, segredos) = Criar("k1");
        segredos.Valores["k2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var dec = new CriptografiaBackupAesGcm(segredos, new OpcoesBackup { ChaveCriptografiaRef = "k2" });

        using var cifrado = new MemoryStream();
        await enc.CriptografarAsync(new MemoryStream(Encoding.UTF8.GetBytes("segredo do contribuinte")), cifrado);
        cifrado.Position = 0;

        var acao = async () => await dec.DecriptografarAsync(cifrado, new MemoryStream());
        await acao.Should().ThrowAsync<CryptographicException>();
    }

    [Fact] // Fase 26 — adulteração do artefato é detectada (tag GCM falha) ⇒ corrupção
    public async Task Cripto_Artefato_Adulterado_Falha()
    {
        var (cripto, _) = Criar("backup-key");
        using var cifrado = new MemoryStream();
        await cripto.CriptografarAsync(new MemoryStream(Encoding.UTF8.GetBytes("conteudo integro")), cifrado);
        var bytes = cifrado.ToArray();
        bytes[^1] ^= 0xFF; // adultera o último byte (ciphertext)

        var acao = async () => await cripto.DecriptografarAsync(new MemoryStream(bytes), new MemoryStream());
        await acao.Should().ThrowAsync<CryptographicException>();
    }

    // ---- Executor físico: fail-closed por padrão (Fases 33/34/37) ----

    [Fact] // documenta honestamente que o executor DEFAULT está BLOCKED (fail-closed) sem infraestrutura
    public async Task Executor_Fisico_Indisponivel_Sem_MySQL()
    {
        var executor = new ExecutorBackupIndisponivel();
        executor.Disponivel.Should().BeFalse();

        var acao = async () => await executor.GerarDumpAsync("tenant_x", new MemoryStream(), CancellationToken.None);
        await acao.Should().ThrowAsync<ExecutorBackupIndisponivelException>();
    }

    [Fact] // Fase 37 — mesmo com o executor real, DESABILITADO ⇒ indisponível (não vira "sucesso" silencioso)
    public void Executor_MySql_Desabilitado_Nao_Fica_Disponivel()
    {
        var executor = CriarExecutorMySql(habilitado: false, adminUsuario: "root", adminSenha: "x");
        executor.Disponivel.Should().BeFalse();
    }

    [Fact] // Fase 37 — habilitado porém SEM usuário admin configurado ⇒ ainda indisponível (fail-closed)
    public void Executor_MySql_Habilitado_Sem_Credencial_Fica_Indisponivel()
    {
        var executor = CriarExecutorMySql(habilitado: true, adminUsuario: "", adminSenha: "");
        executor.Disponivel.Should().BeFalse();
    }

    // ---- Prova §38: os argumentos NUNCA carregam senha/usuário/host na linha de comando ----

    [Fact] // a senha (segredo) e o usuário jamais aparecem entre os argumentos — só via --defaults-extra-file
    public void Argumentos_Nunca_Contem_Senha_Usuario_Host()
    {
        const string senha = "SenhaSecreta_do_Cofre_123";
        const string usuario = "adminbackup";
        const string host = "db.interno.exemplo";
        var arquivo = Path.Combine(Path.GetTempPath(), "portalbase-bkp-fake.cnf");

        var dump = ExecutorBackupBancoMySql.MontarArgumentosDump(arquivo, "tenant_abc");
        var criar = ExecutorBackupBancoMySql.MontarArgumentosCriarBase(arquivo, "tenant_tmp");
        var restaurar = ExecutorBackupBancoMySql.MontarArgumentosRestaurar(arquivo, "tenant_tmp");

        foreach (var args in new[] { dump, criar, restaurar })
        {
            // Credencial só chega por arquivo de opções (§38): a senha/usuário/host NUNCA no command line.
            args.Should().NotContain(a => a.Contains(senha, StringComparison.Ordinal), "senha nunca vai na linha de comando");
            args.Should().NotContain(a => a.Contains(usuario, StringComparison.Ordinal), "usuário nunca vai na linha de comando");
            args.Should().NotContain(a => a.Contains(host, StringComparison.Ordinal), "host nunca vai na linha de comando");
            args.Should().NotContain(a => a.StartsWith("--password", StringComparison.Ordinal) || a == "-p", "sem --password/-p");
            args.Should().NotContain(a => a.StartsWith("--user", StringComparison.Ordinal) || a == "-u", "sem --user/-u");
            args.Should().Contain(a => a.StartsWith("--defaults-extra-file=", StringComparison.Ordinal), "credencial vem por arquivo de opções");
        }
    }

    [Fact] // o dump é SOMENTE de tabelas (sem CREATE DATABASE) ⇒ restaurável em base temporária; nome validado
    public void Argumentos_Dump_Sao_Transacionais_E_Sem_Create_Database()
    {
        var args = ExecutorBackupBancoMySql.MontarArgumentosDump("/tmp/x.cnf", "tenant_xyz");
        args.Should().Contain("--single-transaction");
        args.Should().Contain("--set-gtid-purged=OFF");
        args.Should().Contain("tenant_xyz"); // base posicional = só as tabelas
        args.Should().NotContain(a => a.Contains("--databases", StringComparison.Ordinal)); // nunca --databases (traria CREATE DATABASE)
    }

    [Theory] // §38/§2 — nome de base hostil é rejeitado ANTES de qualquer processo (sem SQL/arg injection)
    [InlineData("tenant`; DROP DATABASE x;--")]
    [InlineData("--all-databases")]
    [InlineData("mysql")]
    [InlineData("../etc")]
    [InlineData("")]
    public async Task Executor_MySql_Rejeita_Base_Hostil(string baseHostil)
    {
        var executor = CriarExecutorMySql(habilitado: true, adminUsuario: "root", adminSenha: "x");
        var gerar = async () => await executor.GerarDumpAsync(baseHostil, new MemoryStream(), CancellationToken.None);
        await gerar.Should().ThrowAsync<Exception>(); // EntradaInvalidaException/ArgumentException — nunca executa
    }

    // ---- Backup/restore FÍSICO real (dump→cifra→hash→decifra→restore temporário→VALIDAÇÃO) — gated ----

    [SkippableFact] // §36/§40/§41 — prova física REAL contra MySQL (nunca mock); sem MySQL o cenário fica BLOCKED
    public async Task Backup_Restore_Fisico_Roundtrip_Prova_Sentinela()
    {
        var conexao = Environment.GetEnvironmentVariable("PORTALBASE_BACKUP_MYSQL");
        Skip.If(string.IsNullOrWhiteSpace(conexao), "MySQL/mysqldump indisponível — backup/restore FÍSICO BLOCKED (Fase 33).");

        var admin = new MySqlConnectionStringBuilder(conexao);
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var baseOrigem = $"pb_bkp_src_{sufixo}";
        var baseTemp = $"pb_bkp_tmp_{sufixo}";
        const decimal saldoSentinela = 1234567.89m; // dado-sentinela: prova que o CONTEÚDO sobreviveu

        try
        {
            // 1. Base de ORIGEM real com uma tabela-sentinela e um saldo conhecido.
            await using (var c = new MySqlConnection(conexao))
            {
                await c.OpenAsync();
                await ExecAsync(c, $"CREATE DATABASE `{baseOrigem}` CHARACTER SET utf8mb4");
                await ExecAsync(c, $"CREATE TABLE `{baseOrigem}`.saldo_sentinela (id INT PRIMARY KEY, valor DECIMAL(18,2) NOT NULL)");
                await ExecAsync(c, $"INSERT INTO `{baseOrigem}`.saldo_sentinela (id, valor) VALUES (1, {saldoSentinela.ToString(CultureInfo.InvariantCulture)})");
            }

            // 2. Executor FÍSICO real (mysqldump/mysql), credencial do "cofre" por arquivo de opções 0600.
            var executor = CriarExecutorMySql(
                habilitado: true,
                adminUsuario: admin.UserID,
                adminSenha: admin.Password,
                servidor: admin.Server,
                porta: (uint)admin.Port,
                sslMode: admin.SslMode);
            executor.Disponivel.Should().BeTrue("mysqldump/mysql presentes e executor habilitado");

            // 3. DUMP físico → CIFRA (AES-256-GCM, chave do cofre) → HASH SHA-256 (o artefato real armazenado).
            var (cripto, _) = Criar("backup-key-fisico");
            byte[] artefatoCifrado;
            byte[] hashArtefato;
            using (var dump = new MemoryStream())
            {
                await executor.GerarDumpAsync(baseOrigem, dump, CancellationToken.None);
                dump.Length.Should().BeGreaterThan(0, "o dump físico não pode ser vazio");
                dump.Position = 0;

                using var cifrado = new MemoryStream();
                await cripto.CriptografarAsync(dump, cifrado);
                artefatoCifrado = cifrado.ToArray();
                hashArtefato = SHA256.HashData(artefatoCifrado);
            }

            // 4. Verificação de integridade ANTES do restore (hash recalculado bate) + DECIFRA.
            SHA256.HashData(artefatoCifrado).Should().Equal(hashArtefato); // Fase 23/25
            using var claro = new MemoryStream();
            await cripto.DecriptografarAsync(new MemoryStream(artefatoCifrado), claro);
            claro.Position = 0;

            // 5. RESTORE em base TEMPORÁRIA (nunca a origem/produção) — BACKUP ≠ RESTORE.
            await executor.RestaurarEmBaseTemporariaAsync(baseTemp, claro, CancellationToken.None);

            // 6. VALIDAÇÃO DEPOIS do restore (§40): a sentinela existe e o saldo é idêntico na base temporária.
            await using (var c = new MySqlConnection(new MySqlConnectionStringBuilder(conexao) { Database = baseTemp }.ConnectionString))
            {
                await c.OpenAsync();
                await using var cmd = new MySqlCommand("SELECT valor FROM saldo_sentinela WHERE id = 1", c);
                var lido = await cmd.ExecuteScalarAsync();
                lido.Should().NotBeNull("a tabela-sentinela deve existir na base restaurada");
                Convert.ToDecimal(lido, CultureInfo.InvariantCulture).Should().Be(saldoSentinela);
            }
        }
        finally
        {
            await using var c = new MySqlConnection(conexao);
            await c.OpenAsync();
            await ExecAsync(c, $"DROP DATABASE IF EXISTS `{baseOrigem}`");
            await ExecAsync(c, $"DROP DATABASE IF EXISTS `{baseTemp}`");
        }
    }

    private static ExecutorBackupBancoMySql CriarExecutorMySql(
        bool habilitado,
        string adminUsuario,
        string adminSenha,
        string servidor = "localhost",
        uint porta = 3306,
        MySqlSslMode sslMode = MySqlSslMode.Disabled)
    {
        var segredos = new ProvedorSegredosFake();
        segredos.Valores["adm"] = adminSenha;
        var banco = new OpcoesBancoTenant
        {
            Servidor = servidor,
            Porta = porta,
            AdminUsuario = adminUsuario,
            AdminSenhaRef = "adm",
            SslMode = sslMode,
        };
        var opcoes = new OpcoesExecutorBackupMySql { Habilitado = habilitado };
        return new ExecutorBackupBancoMySql(Options.Create(banco), Options.Create(opcoes), segredos);
    }

    private static async Task ExecAsync(MySqlConnection conexao, string sql)
    {
        await using var cmd = new MySqlCommand(sql, conexao);
        await cmd.ExecuteNonQueryAsync();
    }
}
