using System.Text.RegularExpressions;
using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Ingestao;
using PortalBase.Web;
using PortalBase.Web.Seguranca;
using PortalBase.Web.Ui.Dados;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// OBS-002 (auditoria de 08/10/2026) — nenhuma exceção da interface fica sem log. Antes, ~90 blocos
/// <c>catch (Exception)</c> das telas trocavam a falha por uma mensagem genérica sem registrar nada no servidor.
/// <list type="bullet">
/// <item>Guarda permanente: varre o código da Web e falha se um <c>catch</c> GENÉRICO (sem filtro, ou só excluindo
/// cancelamento) não chamar <see cref="FalhasUi.Registrar"/> nem um log próprio (exceção explícita: um comentário
/// <c>// sem-log: motivo</c>, para comando de console sem host). Os <c>catch</c> com filtro de exceção
/// ESPERADA (autorização negada, validação) não entram: são respostas de negócio, não falhas.</item>
/// <item>Comportamento: a falha inesperada vira Error no log com componente, operação e tipo; a negativa de autorização
/// não polui o log com erro.</item>
/// </list>
/// </summary>
public sealed class FalhasUiTests
{
    private static readonly Regex Captura = new(
        @"catch\s*\(\s*Exception\s+(?<nome>\w+)\s*\)(?:\s*when\s*\((?<filtro>[^\r\n]*)\))?", RegexOptions.Compiled);

    private static readonly Regex FiltroGenerico = new(
        @"^\s*\w+\s+is\s+not\s+(?:System\.)?(?:OperationCanceledException|OutOfMemoryException)\s*\)?\s*$", RegexOptions.Compiled);

    private static readonly Regex Registro = new(@"FalhasUi\.Registrar\(|\bLog(?:\.\w+|\w*)\(|\b_logger?\b|// sem-log: \w", RegexOptions.Compiled);

    [Fact]
    public void Todo_Catch_Generico_Da_Web_Registra_A_Falha()
    {
        var raiz = Path.Combine(RaizRepositorio(), "src", "PortalBase.Web");
        var semRegistro = new List<string>();
        var generico = 0;
        foreach (var arquivo in Directory.EnumerateFiles(raiz, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var texto = File.ReadAllText(arquivo);
            foreach (Match m in Captura.Matches(texto))
            {
                var filtro = m.Groups["filtro"].Success ? m.Groups["filtro"].Value : string.Empty;
                if (filtro.Length > 0 && !FiltroGenerico.IsMatch(filtro))
                {
                    continue; // exceção esperada (autorização, validação...): resposta de negócio
                }

                generico++;
                if (!Registro.IsMatch(Bloco(texto, m.Index + m.Length)))
                {
                    var linha = texto.AsSpan(0, m.Index).Count('\n') + 1;
                    semRegistro.Add($"{Path.GetRelativePath(raiz, arquivo)}:{linha}");
                }
            }
        }

        generico.Should().BeGreaterThan(20, "a varredura precisa achar os catch genéricos da Web (sanidade do teste)");
        semRegistro.Should().BeEmpty(
            "todo catch genérico da Web registra a falha (FalhasUi.Registrar ou log próprio); sem registro ({0}): {1}",
            semRegistro.Count, string.Join(", ", semRegistro));
    }

    [Fact]
    public async Task Falha_Inesperada_Na_Tela_Vira_Erro_No_Log_Com_Componente_Operacao_E_Tipo()
    {
        var eventos = new ColetorEventos();
        ResultadoListaVm<RegistroArquivoVm> vm;
        using (FalhasUi.Capturar(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(eventos).CreateLogger()))
        {
            vm = await (await DadosAsync(permitir: true)).ConsultarAsync(new FiltroArquivosVm());
        }

        vm.Estado.Should().Be(EstadoConsultaUi.Erro, "o usuário continua vendo a mensagem genérica");
        var evento = eventos.Itens.Should().ContainSingle().Subject;
        evento.Level.Should().Be(LogEventLevel.Error);
        evento.Exception.Should().BeOfType<InvalidOperationException>();
        evento.Properties["Componente"].ToString().Should().Contain("DadosTenantBase");
        evento.Properties["Operacao"].ToString().Should().Contain("ExecutarAsync");
        evento.Properties["TipoErro"].ToString().Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task Autorizacao_Negada_E_Resposta_De_Negocio_E_Nao_Vira_Erro_No_Log()
    {
        var eventos = new ColetorEventos();
        ResultadoListaVm<RegistroArquivoVm> vm;
        using (FalhasUi.Capturar(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(eventos).CreateLogger()))
        {
            vm = await (await DadosAsync(permitir: false)).ConsultarAsync(new FiltroArquivosVm());
        }

        vm.Estado.Should().Be(EstadoConsultaUi.SemPermissao);
        eventos.Itens.Should().BeEmpty();
    }

    // ---- Apoio ----

    private static async Task<DadosArquivos> DadosAsync(bool permitir)
    {
        var tenantId = Guid.NewGuid();
        var usuario = Guid.NewGuid().ToString();
        var selecao = new SelecaoTenant(new ResolvedorOk(), new Seguranca(usuario), new AuditoriaNula(), new Relogio());
        await selecao.HidratarAsync(tenantId);
        var consulta = new ConsultaArquivosImportacaoAutorizada(new TenantContextFake(tenantId), new Autorizacao(permitir), new ConsultaQueFalha());
        return new DadosArquivos(consulta, selecao);
    }

    private sealed class ConsultaQueFalha : IConsultaArquivosImportacao
    {
        public Task<ResultadoPaginado<ItemArquivoImportacao>> ConsultarAsync(FiltroArquivosImportacao filtro, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("banco fora do ar");
    }

    private static string Bloco(string texto, int depoisDoCatch)
    {
        var inicio = texto.IndexOf('{', depoisDoCatch);
        if (inicio < 0)
        {
            return string.Empty;
        }

        var nivel = 0;
        for (var i = inicio; i < texto.Length; i++)
        {
            nivel += texto[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (nivel == 0)
            {
                return texto[inicio..(i + 1)];
            }
        }

        return texto[inicio..];
    }

    private static string RaizRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    private sealed class ColetorEventos : ILogEventSink
    {
        public List<LogEvent> Itens { get; } = [];

        public void Emit(LogEvent logEvent) => Itens.Add(logEvent);
    }

    private sealed class Autorizacao(bool permitir) : IServicoAutorizacao
    {
        public ValueTask<ResultadoAutorizacao> AvaliarAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(permitir ? ResultadoAutorizacao.Ok : ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao));

        public ValueTask ExigirAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => permitir ? ValueTask.CompletedTask : throw new AutorizacaoNegadaException();

        public ValueTask<ResultadoAutorizacao> AvaliarAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => AvaliarAsync(tenantId, permissao, cancellationToken);

        public ValueTask ExigirAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => ExigirAsync(tenantId, permissao, cancellationToken);
    }

    private sealed class ResolvedorOk : IResolvedorTenant
    {
        public ValueTask<ITenantContext> ResolverAsync(string? principalId, Guid tenantId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITenantContext>(new TenantContextFake(tenantId));
    }

    private sealed class TenantContextFake(Guid tenantId) : ITenantContext
    {
        public EstadoTenant Estado => EstadoTenant.Autorizado;
        public string? PrincipalId => "user-1";
        public Guid? TenantId => tenantId;
        public string? NomeBaseFisica => "tenant_x";
        public string? ReferenciaSegredoConexao => "cofre://x";
        public bool EstaAutorizado => true;
    }

    private sealed class Seguranca(string id) : IContextoSeguranca
    {
        public bool EstaAutenticado => true;
        public string? PrincipalId => id;
        public bool MfaSatisfeito => true;
    }

    private sealed class AuditoriaNula : IAuditoria
    {
        public ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Relogio : IRelogio
    {
        public DateTimeOffset AgoraUtc => DateTimeOffset.UnixEpoch;
    }
}
