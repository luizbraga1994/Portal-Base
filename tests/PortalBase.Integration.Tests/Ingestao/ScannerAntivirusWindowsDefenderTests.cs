using System.ComponentModel;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortalBase.Application.Ingestao;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Ingestao;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Ingestao;

/// <summary>
/// Antivírus dos uploads pelo Windows Defender (MpCmdRun): tradução FAIL-CLOSED dos códigos de saída, argumentos fixos
/// (sem shell), cópia temporária privada SEMPRE apagada, nova tentativa só para erro da ferramenta, tempo esgotado e
/// cancelamento, limite de varreduras simultâneas, nada de nome/conteúdo em log, localização do executável e seleção do
/// motor na composição. O MpCmdRun real só existe no Windows: aqui ele é substituído por um executor falso; o executor
/// de processos real é provado com <c>/bin/sh</c>.
/// </summary>
public sealed class ScannerAntivirusWindowsDefenderTests : IDisposable
{
    private const string Executavel = "/opt/defender/MpCmdRun.exe";
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "portalbase-av-" + Guid.NewGuid().ToString("N"));
    private readonly LoggerCaptura<ScannerAntivirusWindowsDefender> _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_raiz))
        {
            Directory.Delete(_raiz, recursive: true);
        }
    }

    // ---------------------------------------------------------------- classificação do MpCmdRun

    [Theory]
    [InlineData(0, "Scan starting...\nScan finished.\nScanning C:\\x\\a.bin found no threats.", ResultadoAntivirus.Limpo)]
    [InlineData(0, "", ResultadoAntivirus.Limpo)]
    [InlineData(2, "Scan starting...\nScan finished.\nScanning C:\\x\\a.bin found 1 threats.", ResultadoAntivirus.Infectado)]
    [InlineData(2, "", ResultadoAntivirus.Infectado)]
    [InlineData(0, "Scanning C:\\x\\a.bin found 3 threats.", ResultadoAntivirus.Infectado)]
    [InlineData(2, "CmdTool: Failed with hr = 0x80508023. Check C:\\Windows\\TEMP\\MpCmdRun.log", ResultadoAntivirus.Erro)]
    [InlineData(0, "CmdTool: Failed with hr = 0x80070005.", ResultadoAntivirus.Erro)]
    [InlineData(1, "", ResultadoAntivirus.Erro)]
    [InlineData(-2147024891, "", ResultadoAntivirus.Erro)]
    public void Classifica_Somente_Zero_Sem_Falha_Como_Limpo(int codigo, string saida, ResultadoAntivirus esperado)
        => ScannerAntivirusWindowsDefender.Classificar(new ResultadoProcessoExterno(codigo, saida)).Should().Be(esperado);

    // ---------------------------------------------------------------- varredura

    [Fact]
    public async Task Arquivo_Limpo_Usa_Argumentos_Fixos_Uma_Copia_Privada_Com_O_Conteudo_E_Apaga_A_Copia()
    {
        var executor = new ExecutorFalso(_ => new ResultadoProcessoExterno(0, "found no threats"));
        using var scanner = Criar(executor);
        var conteudo = Encoding.UTF8.GetBytes("conta;valor\n1.1;10,00");
        using var stream = new MemoryStream(conteudo);
        stream.Position = stream.Length; // o caso de uso pode entregar o stream já lido: a varredura volta ao início

        var resultado = await scanner.EscanearAsync(stream);

        resultado.Should().Be(ResultadoAntivirus.Limpo);
        var chamada = executor.Chamadas.Should().ContainSingle().Subject;
        chamada.Executavel.Should().Be(Executavel);
        chamada.Argumentos.Should().HaveCount(6);
        chamada.Argumentos.Should().StartWith(["-Scan", "-ScanType", "3", "-File"]);
        chamada.Argumentos[^1].Should().Be("-DisableRemediation");
        var copia = chamada.Argumentos[4];
        Path.GetDirectoryName(copia).Should().Be(scanner.DiretorioVarredura);
        Path.GetFileName(copia).Should().MatchRegex("^[0-9a-f]{32}\\.bin$", "o nome é gerado, nunca o do usuário");
        chamada.ConteudoNoMomento.Should().Equal(conteudo);
        File.Exists(copia).Should().BeFalse("a cópia temporária é apagada depois da varredura");
        chamada.Timeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task Ameaca_Encontrada_E_Infectado_Sem_Nome_Nem_Conteudo_No_Log()
    {
        var executor = new ExecutorFalso(_ => new ResultadoProcessoExterno(2, "Scanning x found 1 threats."));
        using var scanner = Criar(executor);

        var resultado = await scanner.EscanearAsync(new MemoryStream(Encoding.UTF8.GetBytes("CONTEUDO-SECRETO")));

        resultado.Should().Be(ResultadoAntivirus.Infectado);
        File.Exists(executor.Chamadas[0].Argumentos[4]).Should().BeFalse();
        _log.Mensagens.Should().Contain(m => m.Contains("ameaça detectada", StringComparison.Ordinal));
        _log.Mensagens.Should().NotContain(m => m.Contains("CONTEUDO-SECRETO", StringComparison.Ordinal)
            || m.Contains(executor.Chamadas[0].Argumentos[4], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Erro_Da_Ferramenta_Tenta_De_Novo_E_Termina_Em_Erro_Nunca_Limpo()
    {
        var executor = new ExecutorFalso(_ => new ResultadoProcessoExterno(2, "CmdTool: Failed with hr = 0x800106BA"));
        using var scanner = Criar(executor);

        var resultado = await scanner.EscanearAsync(new MemoryStream([1, 2, 3]));

        resultado.Should().Be(ResultadoAntivirus.Erro);
        executor.Chamadas.Should().HaveCount(2, "Tentativas = 2 (padrão)");
        _log.Mensagens.Should().Contain(m => m.Contains("0x800106BA", StringComparison.Ordinal) && m.Contains("rejeitado", StringComparison.Ordinal));
        _log.Mensagens.Should().NotContain(m => m.Contains(scanner.DiretorioVarredura, StringComparison.Ordinal));
        Directory.EnumerateFiles(scanner.DiretorioVarredura).Should().BeEmpty();
    }

    [Fact]
    public async Task Erro_Seguido_De_Sucesso_Resulta_No_Sucesso()
    {
        var executor = new ExecutorFalso(n => n == 1 ? new ResultadoProcessoExterno(1, string.Empty) : new ResultadoProcessoExterno(0, string.Empty));
        using var scanner = Criar(executor);

        (await scanner.EscanearAsync(new MemoryStream([1]))).Should().Be(ResultadoAntivirus.Limpo);
        executor.Chamadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task Falha_Ao_Iniciar_O_MpCmdRun_E_Erro_Depois_Das_Tentativas()
    {
        var executor = new ExecutorFalso(_ => throw new Win32Exception(5, "Acesso negado"));
        using var scanner = Criar(executor, new OpcoesAntivirus { Tentativas = 3 });

        (await scanner.EscanearAsync(new MemoryStream([1]))).Should().Be(ResultadoAntivirus.Erro);
        executor.Chamadas.Should().HaveCount(3);
        _log.Mensagens.Should().Contain(m => m.Contains("Win32Exception", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tempo_Esgotado_E_Erro_Sem_Nova_Tentativa()
    {
        var executor = new ExecutorFalso(_ => throw new TimeoutException());
        using var scanner = Criar(executor);

        (await scanner.EscanearAsync(new MemoryStream([1]))).Should().Be(ResultadoAntivirus.Erro);
        executor.Chamadas.Should().ContainSingle();
        _log.Mensagens.Should().Contain(m => m.Contains("não terminou em 120 s", StringComparison.Ordinal));
        Directory.EnumerateFiles(scanner.DiretorioVarredura).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelamento_Do_Pedido_Propaga_E_Apaga_A_Copia()
    {
        using var cts = new CancellationTokenSource();
        var executor = new ExecutorFalso(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var scanner = Criar(executor);

        var acao = async () => await scanner.EscanearAsync(new MemoryStream([1, 2]), cts.Token);

        await acao.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFiles(scanner.DiretorioVarredura).Should().BeEmpty();
    }

    [Fact]
    public async Task Sem_MpCmdRun_Fica_Indisponivel_E_Nao_Executa_Nada()
    {
        var executor = new ExecutorFalso(_ => new ResultadoProcessoExterno(0, string.Empty));
        using var scanner = Criar(executor, localizar: () => null);

        scanner.Disponivel.Should().BeFalse();
        (await scanner.EscanearAsync(new MemoryStream([1]))).Should().Be(ResultadoAntivirus.Indisponivel);
        executor.Chamadas.Should().BeEmpty();
        _log.Mensagens.Should().Contain(m => m.Contains("MpCmdRun.exe) não encontrado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Respeita_O_Limite_De_Varreduras_Simultaneas()
    {
        var emCurso = 0;
        var maximo = 0;
        var executor = ExecutorFalso.Assincrono(async _ =>
        {
            var agora = Interlocked.Increment(ref emCurso);
            InterlockedMax(ref maximo, agora);
            await Task.Delay(50);
            Interlocked.Decrement(ref emCurso);
            return new ResultadoProcessoExterno(0, string.Empty);
        });
        using var scanner = Criar(executor, new OpcoesAntivirus { MaxVarredurasSimultaneas = 1 });

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => scanner.EscanearAsync(new MemoryStream([(byte)i]))));

        resultados.Should().AllBeEquivalentTo(ResultadoAntivirus.Limpo);
        maximo.Should().Be(1);
    }

    [Fact]
    public async Task Opcoes_Fora_Da_Faixa_Sao_Limitadas()
    {
        var executor = new ExecutorFalso(_ => new ResultadoProcessoExterno(1, string.Empty));
        using var scanner = Criar(executor, new OpcoesAntivirus { TimeoutSegundos = 1, Tentativas = 0, MaxVarredurasSimultaneas = 0 });

        (await scanner.EscanearAsync(new MemoryStream([1]))).Should().Be(ResultadoAntivirus.Erro);
        executor.Chamadas.Should().ContainSingle("no mínimo uma tentativa");
        executor.Chamadas[0].Timeout.Should().Be(TimeSpan.FromSeconds(5), "no mínimo 5 s");
    }

    [Fact]
    public void Inicializacao_Apaga_Copias_Orfas_Antigas_E_Preserva_As_Recentes()
    {
        var pasta = Path.Combine(_raiz, "antivirus");
        Directory.CreateDirectory(pasta);
        var orfa = Path.Combine(pasta, Guid.NewGuid().ToString("N") + ".bin");
        var recente = Path.Combine(pasta, Guid.NewGuid().ToString("N") + ".bin");
        var outro = Path.Combine(pasta, "nao-e-copia.txt");
        File.WriteAllText(orfa, "dado do contribuinte");
        File.WriteAllText(recente, "varredura em andamento");
        File.WriteAllText(outro, "x");
        File.SetLastWriteTimeUtc(orfa, DateTime.UtcNow - ScannerAntivirusWindowsDefender.IdadeCopiaOrfa - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(outro, DateTime.UtcNow - TimeSpan.FromDays(1));

        using var scanner = Criar(new ExecutorFalso(_ => new ResultadoProcessoExterno(0, string.Empty)));

        File.Exists(orfa).Should().BeFalse();
        File.Exists(recente).Should().BeTrue();
        File.Exists(outro).Should().BeTrue("só as cópias (*.bin) são apagadas");
    }

    // ---------------------------------------------------------------- localização do MpCmdRun

    [Fact]
    public void Localiza_A_Versao_Mais_Nova_Da_Plataforma_E_Depois_O_Caminho_Classico()
    {
        var programData = Path.Combine(_raiz, "ProgramData");
        var programFiles = Path.Combine(_raiz, "ProgramFiles");
        var plataforma = Path.Combine(programData, "Microsoft", "Windows Defender", "Platform");
        var classico = Criar(Path.Combine(programFiles, "Windows Defender", LocalizadorMpCmdRun.NomeExecutavel));

        LocalizadorMpCmdRun.Localizar(null, programData, programFiles).Should().Be(classico);

        Criar(Path.Combine(plataforma, "4.18.2207.7-0", LocalizadorMpCmdRun.NomeExecutavel));
        var maisNova = Criar(Path.Combine(plataforma, "4.18.24090.11-0", LocalizadorMpCmdRun.NomeExecutavel));
        Criar(Path.Combine(plataforma, "4.18.24090.11-0.old", LocalizadorMpCmdRun.NomeExecutavel)); // nome inválido: ignorado
        Directory.CreateDirectory(Path.Combine(plataforma, "4.18.25000.1-0")); // pasta sem o executável: ignorada

        LocalizadorMpCmdRun.Localizar(null, programData, programFiles).Should().Be(maisNova);
        LocalizadorMpCmdRun.Localizar("  ", programData, programFiles).Should().Be(maisNova);
    }

    [Fact]
    public void Caminho_Configurado_Vale_Sozinho_E_Precisa_Ser_Absoluto_E_Existir()
    {
        var programFiles = Path.Combine(_raiz, "ProgramFiles");
        Criar(Path.Combine(programFiles, "Windows Defender", LocalizadorMpCmdRun.NomeExecutavel));
        var configurado = Criar(Path.Combine(_raiz, "outro", LocalizadorMpCmdRun.NomeExecutavel));

        LocalizadorMpCmdRun.Localizar(configurado, null, programFiles).Should().Be(configurado);
        LocalizadorMpCmdRun.Localizar(Path.Combine(_raiz, "nao-existe.exe"), null, programFiles).Should().BeNull("sem cair para outro executável");
        LocalizadorMpCmdRun.Localizar("MpCmdRun.exe", null, programFiles).Should().BeNull("caminho relativo é recusado");
        LocalizadorMpCmdRun.Localizar(null, null, null).Should().BeNull();
    }

    [Theory]
    [InlineData("4.18.24090.11-0", true)]
    [InlineData("4.18.2207.7", true)]
    [InlineData("4.18.24090.11-x", false)]
    [InlineData("Backup", false)]
    [InlineData("", false)]
    public void Interpreta_A_Versao_Da_Pasta_Da_Plataforma(string nome, bool valida)
        => LocalizadorMpCmdRun.VersaoDaPasta(nome).HasValue.Should().Be(valida);

    // ---------------------------------------------------------------- executor de processos real

    [SkippableFact]
    public async Task Executor_Real_Devolve_Codigo_E_Saida_Sem_Shell()
    {
        Skip.If(OperatingSystem.IsWindows(), "Prova com /bin/sh (Linux).");
        var executor = new ExecutorProcessoExterno();
        var marcador = Path.Combine(_raiz, "injetado");
        Directory.CreateDirectory(_raiz);

        // Cada argumento vai inteiro para o programa: um argumento com ";" e "$(...)" nunca vira comando.
        var resposta = await executor.ExecutarAsync(
            "/bin/sh", ["-c", "echo 'found no threats'; exit 2", $"; touch {marcador}; $(touch {marcador})"], TimeSpan.FromSeconds(10), CancellationToken.None);

        resposta.CodigoSaida.Should().Be(2);
        resposta.Saida.Should().Contain("found no threats");
        File.Exists(marcador).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Executor_Real_Encerra_O_Processo_No_Tempo_Esgotado_E_No_Cancelamento()
    {
        Skip.If(OperatingSystem.IsWindows(), "Prova com /bin/sh (Linux).");
        var executor = new ExecutorProcessoExterno();
        var inicio = DateTimeOffset.UtcNow;

        var esgotado = async () => await executor.ExecutarAsync("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await esgotado.Should().ThrowAsync<TimeoutException>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelado = async () => await executor.ExecutarAsync("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromSeconds(60), cts.Token);
        await cancelado.Should().ThrowAsync<OperationCanceledException>();

        (DateTimeOffset.UtcNow - inicio).Should().BeLessThan(TimeSpan.FromSeconds(20), "o processo é encerrado, não aguardado");
    }

    [SkippableFact]
    public async Task Executor_Real_Limita_A_Saida_Guardada()
    {
        Skip.If(OperatingSystem.IsWindows(), "Prova com /bin/sh (Linux).");
        var executor = new ExecutorProcessoExterno();

        var resposta = await executor.ExecutarAsync("/bin/sh", ["-c", "i=0; while [ $i -lt 3000 ]; do echo 0123456789012345678901234567890123456789; i=$((i+1)); done"], TimeSpan.FromSeconds(30), CancellationToken.None);

        resposta.CodigoSaida.Should().Be(0);
        resposta.Saida.Length.Should().BeLessThanOrEqualTo(ExecutorProcessoExterno.MaximoSaida + 1);
    }

    [SkippableFact]
    public async Task Defender_Real_No_Windows_Considera_Limpo_Um_Arquivo_Inofensivo()
    {
        Skip.IfNot(OperatingSystem.IsWindows() && LocalizadorMpCmdRun.Localizar(null) is not null, "Só com o Microsoft Defender Antivirus (Windows).");
        using var scanner = new ScannerAntivirusWindowsDefender(
            Options.Create(new OpcoesAntivirus()),
            Options.Create(new OpcoesArmazenamento { DiretorioTemporario = _raiz }),
            new ExecutorProcessoExterno(),
            _log);

        (await scanner.EscanearAsync(new MemoryStream(Encoding.UTF8.GetBytes("conta;valor\n1;2")))).Should().Be(ResultadoAntivirus.Limpo);
    }

    // ---------------------------------------------------------------- escolha do motor

    [Theory]
    [InlineData(null, MotorAntivirus.Automatico)]
    [InlineData("", MotorAntivirus.Automatico)]
    [InlineData("windowsdefender", MotorAntivirus.WindowsDefender)]
    [InlineData(" Nenhum ", MotorAntivirus.Nenhum)]
    public void Interpreta_O_Motor(string? valor, MotorAntivirus esperado)
        => OpcoesAntivirus.InterpretarMotor(valor).Should().Be(esperado);

    [Theory]
    [InlineData("ClamAV")]
    [InlineData("2")]
    public void Motor_Desconhecido_Falha_A_Inicializacao(string valor)
    {
        var acao = () => OpcoesAntivirus.InterpretarMotor(valor);
        acao.Should().Throw<InvalidOperationException>().WithMessage("*Antivirus:Motor*");
    }

    [Theory]
    [InlineData(MotorAntivirus.Automatico, true, true)]
    [InlineData(MotorAntivirus.Automatico, false, false)]
    [InlineData(MotorAntivirus.WindowsDefender, false, true)]
    [InlineData(MotorAntivirus.Nenhum, true, false)]
    public void Usa_O_Defender_No_Windows_Ou_Quando_Configurado(MotorAntivirus motor, bool windows, bool esperado)
        => OpcoesAntivirus.UsarWindowsDefender(motor, windows).Should().Be(esperado);

    [Theory]
    [InlineData("WindowsDefender", typeof(ScannerAntivirusWindowsDefender))]
    [InlineData("Nenhum", typeof(ScannerAntivirusIndisponivel))]
    public void Composicao_Registra_O_Motor_Configurado(string motor, Type esperado)
    {
        using var provedor = Runtime(new Dictionary<string, string?>
        {
            ["Antivirus:Motor"] = motor,
            ["Antivirus:CaminhoMpCmdRun"] = Path.Combine(_raiz, "nao-existe", LocalizadorMpCmdRun.NomeExecutavel),
        });

        var scanner = provedor.GetRequiredService<IScannerAntivirus>();

        scanner.Should().BeOfType(esperado);
        scanner.Disponivel.Should().BeFalse("o MpCmdRun configurado não existe: fail-closed");
    }

    [Fact]
    public void Composicao_Automatica_Segue_O_Sistema_Operacional()
    {
        using var provedor = Runtime(new Dictionary<string, string?>());

        provedor.GetRequiredService<IScannerAntivirus>().Should().BeOfType(
            OperatingSystem.IsWindows() ? typeof(ScannerAntivirusWindowsDefender) : typeof(ScannerAntivirusIndisponivel));
    }

    [Fact]
    public void Composicao_Com_Motor_Invalido_Falha()
    {
        var acao = () => Runtime(new Dictionary<string, string?> { ["Antivirus:Motor"] = "Qualquer" }).Dispose();
        acao.Should().Throw<InvalidOperationException>().WithMessage("*Antivirus:Motor*");
    }

    // ---------------------------------------------------------------- apoio

    private ScannerAntivirusWindowsDefender Criar(ExecutorFalso executor, OpcoesAntivirus? opcoes = null, Func<string?>? localizar = null)
        => new(
            Options.Create(opcoes ?? new OpcoesAntivirus()),
            Options.Create(new OpcoesArmazenamento { DiretorioTemporario = _raiz }),
            executor,
            _log,
            localizar ?? (() => Executavel),
            esperaEntreTentativas: TimeSpan.Zero);

    private static string Criar(string caminho)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(caminho, "exe");
        return caminho;
    }

    private ServiceProvider Runtime(Dictionary<string, string?> valores)
    {
        valores["Vault:Modo"] = "ConfiguracaoLocal";
        valores["Armazenamento:DiretorioTemporario"] = _raiz;
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var servicos = new ServiceCollection();
        servicos.AddLogging();
        servicos.AddInfrastructure(configuracao, conexaoObrigatoria: false, producao: false);
        return servicos.BuildServiceProvider();
    }

    private static void InterlockedMax(ref int alvo, int valor)
    {
        int atual;
        while ((atual = Volatile.Read(ref alvo)) < valor && Interlocked.CompareExchange(ref alvo, valor, atual) != atual)
        {
        }
    }

    private sealed record Chamada(string Executavel, IReadOnlyList<string> Argumentos, TimeSpan Timeout, byte[] ConteudoNoMomento);

    /// <summary>MpCmdRun falso: registra a chamada (com o conteúdo da cópia NO MOMENTO da varredura) e responde.</summary>
    private sealed class ExecutorFalso : IExecutorProcessoExterno
    {
        private readonly Func<int, Task<ResultadoProcessoExterno>> _responder;
        private int _numero;

        public ExecutorFalso(Func<int, ResultadoProcessoExterno> responder)
            => _responder = n => Task.FromResult(responder(n));

        private ExecutorFalso(Func<int, Task<ResultadoProcessoExterno>> responder, bool _) => _responder = responder;

        public static ExecutorFalso Assincrono(Func<int, Task<ResultadoProcessoExterno>> responder) => new(responder, true);

        public List<Chamada> Chamadas { get; } = [];

        public async Task<ResultadoProcessoExterno> ExecutarAsync(
            string executavel, IReadOnlyList<string> argumentos, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var arquivo = argumentos.Count > 4 ? argumentos[4] : string.Empty;
            var conteudo = File.Exists(arquivo) ? await File.ReadAllBytesAsync(arquivo, cancellationToken) : [];
            int numero;
            lock (Chamadas)
            {
                Chamadas.Add(new Chamada(executavel, [.. argumentos], timeout, conteudo));
                numero = ++_numero;
            }

            return await _responder(numero);
        }
    }
}
