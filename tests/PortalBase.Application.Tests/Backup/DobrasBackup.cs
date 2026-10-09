using System.Collections.Concurrent;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Backup;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Backup;

/// <summary>Contexto de segurança configurável para os testes de backup/restore.</summary>
internal sealed class ContextoSegurancaFake : IContextoSeguranca
{
    public bool EstaAutenticado { get; set; } = true;

    public string? PrincipalId { get; set; } = "op-plataforma";

    public bool MfaSatisfeito { get; set; } = true;
}

/// <summary>Autorização de plataforma configurável (deny-by-default nos testes que negam).</summary>
internal sealed class AutorizacaoPlataformaFake : IAutorizacaoPlataforma
{
    public bool Permite { get; set; } = true;

    public int Chamadas { get; private set; }

    public ValueTask<ResultadoAutorizacao> AvaliarAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Permite ? ResultadoAutorizacao.Ok : ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao));

    public ValueTask ExigirAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        if (!Permite)
        {
            throw new AutorizacaoNegadaException();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Catálogo de backups em memória.</summary>
internal sealed class RepositorioRegistroBackupMemoria : IRepositorioRegistroBackup
{
    public ConcurrentDictionary<Guid, RegistroBackup> Registros { get; } = new();

    public Task AdicionarAsync(RegistroBackup registro, CancellationToken cancellationToken = default)
    {
        Registros[registro.Id] = registro;
        return Task.CompletedTask;
    }

    public Task AtualizarAsync(RegistroBackup registro, CancellationToken cancellationToken = default)
    {
        Registros[registro.Id] = registro;
        return Task.CompletedTask;
    }

    public Task<RegistroBackup?> ObterAsync(Guid backupId, CancellationToken cancellationToken = default)
        => Task.FromResult(Registros.GetValueOrDefault(backupId));

    public Task<IReadOnlyList<RegistroBackup>> ListarPorTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RegistroBackup>>(Registros.Values.Where(r => r.TenantId == tenantId).ToList());

    public Task<IReadOnlyList<RegistroBackup>> ListarConcluidosAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RegistroBackup>>(Registros.Values.Where(r => r.Status == StatusBackup.Concluido).ToList());

    public Task<int> ContarConcluidosAsync(Guid? tenantId, TipoBackup tipo, CancellationToken cancellationToken = default)
        => Task.FromResult(Registros.Values.Count(r => r.Tipo == tipo && r.Status == StatusBackup.Concluido && r.TenantId == tenantId));

    public Task<bool> ExisteConcluidoOuEmAndamentoDesdeAsync(
        TipoBackup tipo, DateTimeOffset desdeUtc, DateTimeOffset emAndamentoDesdeUtc, CancellationToken cancellationToken = default)
        => Task.FromResult(Registros.Values.Any(r => r.Tipo == tipo && r.IniciadoEmUtc >= desdeUtc
            && (r.Status == StatusBackup.Concluido || (r.Status == StatusBackup.Iniciado && r.IniciadoEmUtc >= emAndamentoDesdeUtc))));

    public Task<IReadOnlyList<RegistroBackup>> ListarIniciadosAntesDeAsync(DateTimeOffset antesDeUtc, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RegistroBackup>>(
            Registros.Values.Where(r => r.Status == StatusBackup.Iniciado && r.IniciadoEmUtc < antesDeUtc).ToList());

    public Task<IReadOnlyList<RegistroBackup>> ListarConcluidosVencidosAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RegistroBackup>>(
            Registros.Values.Where(r => r.Status == StatusBackup.Concluido && r.RetencaoAteUtc is { } ate && ate <= agoraUtc).ToList());

    public Task<IReadOnlyList<RegistroBackup>> ListarRecentesAsync(int quantidade, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RegistroBackup>>(Registros.Values.OrderByDescending(r => r.IniciadoEmUtc).Take(quantidade).ToList());
}

/// <summary>Store de artefatos em memória (permite adulterar bytes para o teste de corrupção).</summary>
internal sealed class ArmazenamentoBackupMemoria : IArmazenamentoBackup
{
    public ConcurrentDictionary<string, byte[]> Artefatos { get; } = new(StringComparer.Ordinal);

    public async Task<string> GravarAsync(string referenciaLogica, Stream conteudo, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await conteudo.CopyToAsync(ms, cancellationToken);
        Artefatos[referenciaLogica] = ms.ToArray();
        return referenciaLogica;
    }

    public Task<Stream> AbrirAsync(string referenciaLogica, CancellationToken cancellationToken = default)
        => Artefatos.TryGetValue(referenciaLogica, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new InvalidOperationException("Artefato inexistente.");

    public Task<bool> ExisteAsync(string referenciaLogica, CancellationToken cancellationToken = default)
        => Task.FromResult(Artefatos.ContainsKey(referenciaLogica));

    /// <summary>Simula disco/compartilhamento que recusa apagar (a retenção deve manter o registro Concluído).</summary>
    public bool FalharRemocao { get; set; }

    public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default)
    {
        if (FalharRemocao)
        {
            throw new IOException("arquivo em uso");
        }

        Artefatos.TryRemove(referenciaLogica, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Relógio que o teste avança (retenção, interrompidos).</summary>
internal sealed class RelogioAjustavel(DateTimeOffset agora) : PortalBase.Application.Abstracoes.IRelogio
{
    public DateTimeOffset AgoraUtc { get; set; } = agora;
}

/// <summary>Cifra "identidade" (passa direto) — isola o pipeline sem depender de chave real.</summary>
internal sealed class CriptografiaBackupIdentidade : ICriptografiaBackup
{
    public string ChaveRef => "backup-key-ref-teste";

    public Task CriptografarAsync(Stream claro, Stream cifradoDestino, CancellationToken cancellationToken = default)
        => claro.CopyToAsync(cifradoDestino, cancellationToken);

    public Task DecriptografarAsync(Stream cifrado, Stream claroDestino, CancellationToken cancellationToken = default)
        => cifrado.CopyToAsync(claroDestino, cancellationToken);
}

/// <summary>Executor físico fake — mede a concorrência máxima e produz um dump determinístico por base.</summary>
internal sealed class ExecutorBackupFake : IExecutorBackupBanco
{
    private int _emExecucao;

    public bool Disponivel { get; set; } = true;

    /// <summary>Simula falha do dump (ex.: mysqldump com erro) — o motivo registrado é só o tipo do erro.</summary>
    public bool FalharDump { get; set; }

    /// <summary>Simula o TEMPO ESGOTADO do mysqldump: o executor encerra o processo e lança cancelamento próprio.</summary>
    public bool EsgotarTempo { get; set; }

    public int ConcorrenciaMaxima { get; private set; }

    public ConcurrentBag<string> BasesRestauradas { get; } = new();

    public async Task GerarDumpAsync(string nomeBaseLogica, Stream destino, CancellationToken cancellationToken = default)
    {
        if (FalharDump)
        {
            throw new IOException($"falha ao gerar dump de {nomeBaseLogica}");
        }

        if (EsgotarTempo)
        {
            throw new OperationCanceledException("tempo esgotado");
        }

        var atual = Interlocked.Increment(ref _emExecucao);
        ConcorrenciaMaxima = Math.Max(ConcorrenciaMaxima, atual);
        try
        {
            await Task.Delay(15, cancellationToken);
            var bytes = System.Text.Encoding.UTF8.GetBytes($"DUMP::{nomeBaseLogica}");
            await destino.WriteAsync(bytes, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _emExecucao);
        }
    }

    public Task RestaurarEmBaseTemporariaAsync(string nomeBaseTemporaria, Stream origem, CancellationToken cancellationToken = default)
    {
        BasesRestauradas.Add(nomeBaseTemporaria);
        return Task.CompletedTask;
    }
}

/// <summary>Área temporária em memória (o conteúdo do backup passa por ela — JOB-002).</summary>
internal sealed class AreaTemporariaMemoria : IAreaTrabalhoTemporaria
{
    public int Criados { get; private set; }

    public Task<Stream> CriarAsync(CancellationToken cancellationToken = default)
    {
        Criados++;
        return Task.FromResult<Stream>(new MemoryStream());
    }
}

/// <summary>Cópia externa (offsite) em memória; pode simular falha da cópia ou ficar "não configurada".</summary>
internal sealed class CopiaExternaMemoria : ICopiaExternaBackup
{
    public bool Configurada { get; set; } = true;

    public bool Falhar { get; set; }

    public ConcurrentDictionary<string, byte[]> Copias { get; } = new(StringComparer.Ordinal);

    public async Task CopiarAsync(string referenciaLogica, Stream artefato, string hashSha256Esperado, CancellationToken cancellationToken = default)
    {
        if (Falhar)
        {
            throw new IOException("destino externo fora do ar");
        }

        using var ms = new MemoryStream();
        await artefato.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        var hash = await HashBackup.CalcularAsync(ms, cancellationToken);
        if (!string.Equals(hash, hashSha256Esperado, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("cópia divergente");
        }

        Copias[referenciaLogica] = ms.ToArray();
    }

    public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default)
    {
        Copias.TryRemove(referenciaLogica, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Empacotador de arquivos fake: conteúdo determinístico por contribuinte.</summary>
internal sealed class EmpacotadorArquivosFake : IEmpacotadorArquivosTenant
{
    public bool Falhar { get; set; }

    public ConcurrentBag<Guid> Empacotados { get; } = new();

    public async Task EmpacotarAsync(Guid tenantId, Stream destino, CancellationToken cancellationToken = default)
    {
        if (Falhar)
        {
            throw new IOException("disco");
        }

        Empacotados.Add(tenantId);
        await destino.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"ZIP::{tenantId:N}"), cancellationToken);
    }
}

/// <summary>Provedor de alvos configurável.</summary>
internal sealed class ProvedorAlvosBackupFake : IProvedorAlvosBackup
{
    public List<AlvoBackupTenant> Alvos { get; } = [];

    public Task<IReadOnlyList<AlvoBackupTenant>> ListarBackupReadyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AlvoBackupTenant>>(Alvos);

    public Task<AlvoBackupTenant?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(Alvos.FirstOrDefault(a => a.TenantId == tenantId));

    public string? NomeBaseCatalogo { get; set; } = "portalbase_catalogo";
}

/// <summary>Detector de migration configurável (Fase 8).</summary>
internal sealed class DetectorMigracaoFake : IDetectorMigracaoEmAndamento
{
    public bool EmAndamento { get; set; }

    public Task<bool> MigracaoEmAndamentoAsync(Guid? tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(EmAndamento);
}

/// <summary>Fábrica dos alvos provisionados/nao-provisionados usados nos testes.</summary>
internal static class AlvosBackup
{
    public static AlvoBackupTenant Provisionado(Guid id, string? nomeBase = null)
        => new(id, nomeBase ?? $"tenant_{id:N}", EstadoProvisionamento.Ativo);

    public static AlvoBackupTenant Provisionando(Guid id)
        => new(id, $"tenant_{id:N}", EstadoProvisionamento.Provisionando);
}
