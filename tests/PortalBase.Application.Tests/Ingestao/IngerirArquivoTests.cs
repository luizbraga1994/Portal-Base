using System.Text;
using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Arquivos;

namespace PortalBase.Application.Tests.Ingestao;

/// <summary>
/// Matriz do caso de uso de INGESTÃO SEGURA (Prompt 08): caminho feliz e TODOS os caminhos não
/// felizes (vazio, extensão fora da allowlist, tamanho excedido, conteúdo inválido, duplicidade,
/// infectado, antivírus indisponível+obrigatório = fail-closed, corrida de unicidade, tenant não
/// autorizado, autorização negada). Prova os controles com dobras determinísticas — sem rede,
/// sem antivírus real, sem segredos.
/// </summary>
public sealed class IngerirArquivoTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Usuario = Guid.NewGuid();

    private readonly ContextoTenantFake _tenant = new() { TenantId = Tenant };
    private readonly ContextoSegurancaFake _seguranca = new() { PrincipalId = Usuario.ToString() };
    private readonly ServicoAutorizacaoFake _autorizacao = new();
    private readonly AreaTrabalhoTemporariaFake _area = new();
    private readonly ValidadorConteudoFake _validador = new() { ContentType = "text/csv" };
    private readonly ScannerAntivirusFake _antivirus = new();
    private readonly RepositorioArquivoImportacaoFake _repositorio = new();
    private readonly ArmazenamentoArquivosFake _armazenamento = new();
    private readonly AuditoriaFake _auditoria = new();
    private readonly OpcoesUpload _opcoes = new();

    private IngerirArquivo Criar() => new(
        _tenant, _seguranca, _autorizacao, _area, _validador, _antivirus, _repositorio,
        _armazenamento, _auditoria, new ContextoAuditoriaFake(), new PoliticaRetencao(),
        new RelogioFixo(Momento), _opcoes);

    private static ComandoIngestao Comando(string nome, string conteudo)
        => new(nome, new MemoryStream(Encoding.UTF8.GetBytes(conteudo)));

    private bool TemEvento(string acao) => _auditoria.Eventos.Exists(e => e.Operacao == acao);

    [Fact]
    public async Task Arquivo_Valido_E_Aceito_Armazenado_E_Auditado()
    {
        _antivirus.Disponivel = true;
        _antivirus.Resultado = ResultadoAntivirus.Limpo;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Aceito);
        resultado.ArquivoId.Should().NotBeNull();

        _armazenamento.Salvos.Should().ContainSingle();
        _armazenamento.Salvos.Keys.Single().Should().StartWith($"{Tenant:N}/");

        _repositorio.Persistidos.Should().ContainSingle();
        var metadado = _repositorio.Persistidos[0];
        metadado.Estado.Should().Be(EstadoArquivo.Armazenado);
        metadado.ResultadoScan.Should().Be(ResultadoScan.Limpo);
        metadado.Extensao.Should().Be("csv");
        metadado.ContentTypeDetectado.Should().Be("text/csv");
        metadado.Sha256.Should().HaveLength(64);
        metadado.PodeProcessar.Should().BeTrue();

        TemEvento(AcaoAuditoria.UploadStarted).Should().BeTrue();
        TemEvento(AcaoAuditoria.UploadStored).Should().BeTrue();
    }

    [Fact]
    public async Task Arquivo_Vazio_E_Rejeitado_Sem_Armazenar()
    {
        var resultado = await Criar().ExecutarAsync(new ComandoIngestao("vazio.csv", new MemoryStream()), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Rejeitado);
        _armazenamento.Salvos.Should().BeEmpty();
        _repositorio.Persistidos.Should().BeEmpty();
        TemEvento(AcaoAuditoria.UploadRejected).Should().BeTrue();
    }

    [Fact]
    public async Task Extensao_Fora_Da_Allowlist_E_Rejeitada()
    {
        var resultado = await Criar().ExecutarAsync(Comando("malware.exe", "conteudo"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Rejeitado);
        _armazenamento.Salvos.Should().BeEmpty();
        TemEvento(AcaoAuditoria.UploadRejected).Should().BeTrue();
    }

    [Fact]
    public async Task Tamanho_Excedido_E_Rejeitado()
    {
        _opcoes.MaxTamanhoBytes = 4;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "conteudo bem maior que o limite"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Rejeitado);
        _armazenamento.Salvos.Should().BeEmpty();
    }

    [Fact]
    public async Task Conteudo_Invalido_E_Rejeitado()
    {
        _validador.Valido = false;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Rejeitado);
        _armazenamento.Salvos.Should().BeEmpty();
        _repositorio.Persistidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Segundo_Envio_Do_Mesmo_Conteudo_E_Idempotente()
    {
        _antivirus.Disponivel = true;

        var primeiro = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);
        var segundo = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        primeiro.Situacao.Should().Be(SituacaoIngestao.Aceito);
        segundo.Situacao.Should().Be(SituacaoIngestao.Duplicado);
        segundo.ArquivoId.Should().Be(primeiro.ArquivoId, "o reenvio aponta o arquivo já recebido (para processá-lo pela tela)");
        _armazenamento.Salvos.Should().ContainSingle(); // o duplicado não armazena de novo
        TemEvento(AcaoAuditoria.UploadDuplicated).Should().BeTrue();
    }

    [Fact]
    public async Task Arquivo_Infectado_E_Rejeitado_E_Nao_Armazenado()
    {
        _antivirus.Disponivel = true;
        _antivirus.Resultado = ResultadoAntivirus.Infectado;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Rejeitado);
        _armazenamento.Salvos.Should().BeEmpty(); // malware nunca vai para a área privada

        _repositorio.Persistidos.Should().ContainSingle();
        _repositorio.Persistidos[0].Estado.Should().Be(EstadoArquivo.Rejeitado);
        _repositorio.Persistidos[0].ResultadoScan.Should().Be(ResultadoScan.Infectado);
        _repositorio.Persistidos[0].ReferenciaArmazenamento.Should().BeNull();
        TemEvento(AcaoAuditoria.UploadInfected).Should().BeTrue();
    }

    [Fact]
    public async Task Antivirus_Indisponivel_E_Obrigatorio_Rejeita_FailClosed()
    {
        _antivirus.Disponivel = false;
        _opcoes.AntivirusObrigatorio = true;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Rejeitado);
        _armazenamento.Salvos.Should().BeEmpty();
        _repositorio.Persistidos.Should().BeEmpty(); // não persiste → permite reenvio quando o scanner voltar
    }

    [Fact]
    public async Task Antivirus_Indisponivel_E_Nao_Obrigatorio_Armazena_Como_Indisponivel()
    {
        _antivirus.Disponivel = false;
        _opcoes.AntivirusObrigatorio = false;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Aceito);
        _repositorio.Persistidos.Should().ContainSingle();
        _repositorio.Persistidos[0].ResultadoScan.Should().Be(ResultadoScan.Indisponivel);
        _repositorio.Persistidos[0].PodeProcessar.Should().BeFalse(); // não-limpo não é processável
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Falha_Da_Varredura_Rejeita_Sem_Persistir_Mesmo_Sem_Antivirus_Obrigatorio(bool obrigatorio)
    {
        // Ex.: o Windows Defender respondeu com erro ou estourou o tempo. Nunca "limpo"; e, como um arquivo com
        // varredura em erro jamais é processável, rejeita já no envio (o usuário reenvia quando o antivírus voltar).
        _antivirus.Disponivel = true;
        _antivirus.Resultado = ResultadoAntivirus.Erro;
        _opcoes.AntivirusObrigatorio = obrigatorio;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Rejeitado);
        _armazenamento.Salvos.Should().BeEmpty();
        _repositorio.Persistidos.Should().BeEmpty();
        _antivirus.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task Tenant_Nao_Autorizado_Falha_FailClosed()
    {
        _tenant.Estado = EstadoTenant.Invalido;

        var acao = async () => await Criar().ExecutarAsync(Comando("dados.csv", "a,b"), CancellationToken.None);

        await acao.Should().ThrowAsync<TenantContextAusenteException>();
        _autorizacao.Chamadas.Should().Be(0); // nem chega a avaliar permissão
    }

    [Fact]
    public async Task Autorizacao_Negada_Bloqueia_Ingestao()
    {
        _autorizacao.Permitir = false;

        var acao = async () => await Criar().ExecutarAsync(Comando("dados.csv", "a,b"), CancellationToken.None);

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        _armazenamento.Salvos.Should().BeEmpty();
        _repositorio.Persistidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Corrida_De_Unicidade_No_Salvar_Vira_Duplicado_E_Remove_Blob()
    {
        _antivirus.Disponivel = true;
        _repositorio.LancarUnicidadeNoSalvar = true;

        var resultado = await Criar().ExecutarAsync(Comando("dados.csv", "a,b\n1,2"), CancellationToken.None);

        resultado.Situacao.Should().Be(SituacaoIngestao.Duplicado);
        _armazenamento.Removidos.Should().ContainSingle(); // blob órfão removido
        _armazenamento.Salvos.Should().BeEmpty();
        TemEvento(AcaoAuditoria.UploadDuplicated).Should().BeTrue();
    }
}
