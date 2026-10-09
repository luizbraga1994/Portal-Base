using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Backup;
using PortalBase.Application.Ingestao;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Teste permanente (regra 37) de um defeito achado na Onda 2 (08/10/2026): o backup das bases dos contribuintes roda
/// até <c>Backup:ParalelismoMaximo</c> (2) ao mesmo tempo, e todos gravam o registro pelo MESMO repositório do escopo — o
/// mesmo DbContext do catálogo, que não aceita duas operações simultâneas. Com o repositório REAL sobre o MySQL, todos
/// os contribuintes precisam terminar <c>Concluido</c>, com o registro gravado no catálogo.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class BackupParaleloCatalogoMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public BackupParaleloCatalogoMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Backup_Paralelo_Dos_Contribuintes_Grava_Todos_Os_Registros_Sem_Colidir_No_Catalogo()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var tenants = Enumerable.Range(0, 6)
            .Select(i => new AlvoBackupTenant(Guid.NewGuid(), $"portalbase_tenant_par{i}", EstadoProvisionamento.Ativo))
            .ToList();
        var opcoes = new OpcoesBackup { CriptografarEmRepouso = false, ParalelismoMaximo = 2 };
        using var escopo = _fixture.CriarEscopo();
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioRegistroBackup>();
        var servico = new ServicoBackup(
            new Alvos(tenants), new ExecutorLento(), new StoreMemoria(), new SemCifra(), repositorio, new SemMigracao(),
            new PoliticaRetencaoBackup(opcoes), new Permitir(), new AuditoriaNula(), new Seguranca(), new Relogio(), opcoes,
            new AreaMemoria(), new SemCopiaExterna(), new EmpacotadorFixo());

        var resultados = await servico.ExecutarBackupTenantsAsync(PeriodicidadeBackup.Diario);

        resultados.Should().HaveCount(6);
        resultados.Should().OnlyContain(r => r.Status == StatusBackup.Concluido, "nenhum backup pode falhar por colisão no DbContext");
        using var leitura = _fixture.CriarEscopo();
        var gravados = leitura.ServiceProvider.GetRequiredService<IRepositorioRegistroBackup>();
        foreach (var r in resultados)
        {
            (await gravados.ObterAsync(r.BackupId))!.Status.Should().Be(StatusBackup.Concluido);
        }
    }

    // ---- Dobras (o que não é o catálogo) ----

    private sealed class Alvos(List<AlvoBackupTenant> tenants) : IProvedorAlvosBackup
    {
        public string? NomeBaseCatalogo => "portalbase_catalogo";

        public Task<IReadOnlyList<AlvoBackupTenant>> ListarBackupReadyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlvoBackupTenant>>(tenants);

        public Task<AlvoBackupTenant?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(tenants.FirstOrDefault(t => t.TenantId == tenantId));
    }

    /// <summary>Dump com atraso: garante que dois backups fiquem em andamento ao mesmo tempo.</summary>
    private sealed class ExecutorLento : IExecutorBackupBanco
    {
        public bool Disponivel => true;

        public async Task GerarDumpAsync(string nomeBaseLogica, Stream destino, CancellationToken cancellationToken = default)
        {
            await Task.Delay(30, cancellationToken);
            await destino.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"-- dump {nomeBaseLogica}"), cancellationToken);
        }

        public Task RestaurarEmBaseTemporariaAsync(string nomeBaseTemporaria, Stream origem, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StoreMemoria : IArmazenamentoBackup
    {
        private readonly ConcurrentDictionary<string, byte[]> _artefatos = new(StringComparer.Ordinal);

        public async Task<string> GravarAsync(string referenciaLogica, Stream conteudo, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await conteudo.CopyToAsync(ms, cancellationToken);
            await Task.Delay(10, cancellationToken);
            _artefatos[referenciaLogica] = ms.ToArray();
            return referenciaLogica;
        }

        public Task<Stream> AbrirAsync(string referenciaLogica, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(_artefatos[referenciaLogica]));

        public Task<bool> ExisteAsync(string referenciaLogica, CancellationToken cancellationToken = default)
            => Task.FromResult(_artefatos.ContainsKey(referenciaLogica));

        public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default)
        {
            _artefatos.TryRemove(referenciaLogica, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class SemCifra : ICriptografiaBackup
    {
        public string ChaveRef => "ref";

        public Task CriptografarAsync(Stream claro, Stream cifradoDestino, CancellationToken cancellationToken = default)
            => claro.CopyToAsync(cifradoDestino, cancellationToken);

        public Task DecriptografarAsync(Stream cifrado, Stream claroDestino, CancellationToken cancellationToken = default)
            => cifrado.CopyToAsync(claroDestino, cancellationToken);
    }

    private sealed class SemMigracao : IDetectorMigracaoEmAndamento
    {
        public Task<bool> MigracaoEmAndamentoAsync(Guid? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class Permitir : IAutorizacaoPlataforma
    {
        public ValueTask<ResultadoAutorizacao> AvaliarAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ResultadoAutorizacao.Ok);

        public ValueTask ExigirAsync(string permissaoGlobal, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class AuditoriaNula : IAuditoria
    {
        public ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Seguranca : IContextoSeguranca
    {
        public bool EstaAutenticado => true;

        public string? PrincipalId => ExecucaoBackupAgendado.PrincipalSistema;

        public bool MfaSatisfeito => false;
    }

    private sealed class Relogio : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; } = new DateTimeOffset(2091, 1, 1, 3, 0, 0, TimeSpan.Zero).AddDays(Random.Shared.Next(0, 10_000));
    }

    private sealed class AreaMemoria : IAreaTrabalhoTemporaria
    {
        public Task<Stream> CriarAsync(CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class SemCopiaExterna : ICopiaExternaBackup
    {
        public bool Configurada => false;

        public Task CopiarAsync(string referenciaLogica, Stream artefato, string hashSha256Esperado, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("não configurada");

        public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmpacotadorFixo : IEmpacotadorArquivosTenant
    {
        public Task EmpacotarAsync(Guid tenantId, Stream destino, CancellationToken cancellationToken = default)
            => destino.WriteAsync("ZIP"u8.ToArray(), cancellationToken).AsTask();
    }
}
