using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Arquivos;

namespace PortalBase.Application.Tests.Dobras;

/// <summary>Contexto de segurança configurável para os testes do caso de uso de ingestão.</summary>
internal sealed class ContextoSegurancaFake : IContextoSeguranca
{
    public bool EstaAutenticado { get; set; } = true;

    public string? PrincipalId { get; set; }

    public bool MfaSatisfeito { get; set; }
}

/// <summary>Contexto de tenant configurável (autorizado por padrão) para os testes de ingestão.</summary>
internal sealed class ContextoTenantFake : ITenantContext
{
    public EstadoTenant Estado { get; set; } = EstadoTenant.Autorizado;

    public string? PrincipalId { get; set; }

    public Guid? TenantId { get; set; }

    public string? NomeBaseFisica { get; set; } = "tenant_x";

    public string? ReferenciaSegredoConexao { get; set; } = "cofre://x";

    public bool EstaAutorizado => Estado == EstadoTenant.Autorizado;
}

/// <summary>Autorização configurável: permite ou lança a exceção do motivo informado.</summary>
internal sealed class ServicoAutorizacaoFake : IServicoAutorizacao
{
    public bool Permitir { get; set; } = true;

    /// <summary>Permissões negadas mesmo com <see cref="Permitir"/> (perfil sem uma permissão específica).</summary>
    public HashSet<string> Negadas { get; } = new(StringComparer.Ordinal);

    public int Chamadas { get; private set; }

    public ValueTask<ResultadoAutorizacao> AvaliarAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Permitir && !Negadas.Contains(permissao) ? ResultadoAutorizacao.Ok : ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao));

    public ValueTask ExigirAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        if (!Permitir || Negadas.Contains(permissao))
        {
            throw new AutorizacaoNegadaException();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ResultadoAutorizacao> AvaliarAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        => AvaliarAsync(tenantId, permissao, cancellationToken);

    public ValueTask ExigirAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        => ExigirAsync(tenantId, permissao, cancellationToken);
}

/// <summary>Área temporária em memória (stream seekable) para hash/validação/scan em passes.</summary>
internal sealed class AreaTrabalhoTemporariaFake : IAreaTrabalhoTemporaria
{
    public Task<Stream> CriarAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(new MemoryStream());
}

/// <summary>Validador de conteúdo configurável (aceita/rejeita).</summary>
internal sealed class ValidadorConteudoFake : IValidadorConteudoArquivo
{
    public bool Valido { get; set; } = true;

    public string ContentType { get; set; } = "application/octet-stream";

    public Task<ResultadoValidacaoConteudo> ValidarAsync(Stream conteudo, string extensao, CancellationToken cancellationToken = default)
        => Task.FromResult(Valido
            ? ResultadoValidacaoConteudo.Ok(ContentType)
            : ResultadoValidacaoConteudo.Invalido("conteudo_invalido"));
}

/// <summary>Scanner antivírus configurável (disponibilidade + resultado).</summary>
internal sealed class ScannerAntivirusFake : IScannerAntivirus
{
    public bool Disponivel { get; set; }

    public ResultadoAntivirus Resultado { get; set; } = ResultadoAntivirus.Limpo;

    public int Chamadas { get; private set; }

    public Task<ResultadoAntivirus> EscanearAsync(Stream conteudo, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        return Task.FromResult(Resultado);
    }
}

/// <summary>Repositório de metadados em memória, com idempotência por hash e simulação de corrida.</summary>
internal sealed class RepositorioArquivoImportacaoFake : IRepositorioArquivoImportacao
{
    private readonly HashSet<string> _hashes = new(StringComparer.Ordinal);

    public List<ArquivoImportacao> Persistidos { get; } = [];

    public ArquivoImportacao? Pendente { get; private set; }

    /// <summary>Simula outra transação vencendo a corrida (violação de unicidade no SalvarAsync).</summary>
    public bool LancarUnicidadeNoSalvar { get; set; }

    public void SemearHash(string sha256) => _hashes.Add(sha256);

    /// <summary>Simula outra operação alterando o arquivo antes (reivindicação perdida no TentarSalvarAsync).</summary>
    public bool PerderCorridaNoTentarSalvar { get; set; }

    public int TentativasSalvar { get; private set; }

    public Task<bool> ExisteComHashAsync(string sha256, CancellationToken cancellationToken = default)
        => Task.FromResult(_hashes.Contains(sha256));

    public Task<Guid?> ObterIdPorHashAsync(string sha256, CancellationToken cancellationToken = default)
        => Task.FromResult(_hashes.Contains(sha256)
            ? Persistidos.Find(a => string.Equals(a.Sha256, sha256, StringComparison.Ordinal))?.Id ?? IdSemeado
            : (Guid?)null);

    /// <summary>Id devolvido para um hash semeado sem arquivo persistido (envio anterior simulado).</summary>
    public Guid IdSemeado { get; } = Guid.NewGuid();

    public Task<ArquivoImportacao?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Persistidos.Find(a => a.Id == id));

    public Task<bool> TentarSalvarAsync(CancellationToken cancellationToken = default)
    {
        TentativasSalvar++;
        return Task.FromResult(!PerderCorridaNoTentarSalvar);
    }

    public Task AdicionarAsync(ArquivoImportacao arquivo, CancellationToken cancellationToken = default)
    {
        Pendente = arquivo;
        return Task.CompletedTask;
    }

    public Task SalvarAsync(CancellationToken cancellationToken = default)
    {
        if (LancarUnicidadeNoSalvar)
        {
            throw new ViolacaoUnicidadeException();
        }

        if (Pendente is not null)
        {
            _hashes.Add(Pendente.Sha256);
            Persistidos.Add(Pendente);
            Pendente = null;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Armazenamento em memória: registra o que foi salvo e o que foi removido.</summary>
internal sealed class ArmazenamentoArquivosFake : IArmazenamentoArquivos
{
    public Dictionary<string, byte[]> Salvos { get; } = new(StringComparer.Ordinal);

    public List<string> Removidos { get; } = [];

    public async ValueTask<string> SalvarAsync(string caminhoRelativo, Stream conteudo, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await conteudo.CopyToAsync(buffer, cancellationToken);
        Salvos[caminhoRelativo] = buffer.ToArray();
        return caminhoRelativo;
    }

    public ValueTask<Stream> AbrirLeituraAsync(string referencia, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<Stream>(new MemoryStream(Salvos[referencia]));

    public ValueTask RemoverAsync(string referencia, CancellationToken cancellationToken = default)
    {
        Removidos.Add(referencia);
        Salvos.Remove(referencia);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Contexto de correlação de auditoria fixo para os testes.</summary>
internal sealed class ContextoAuditoriaFake : IContextoAuditoria
{
    public string? CorrelationId { get; set; } = "corr-teste";

    public string? TraceId => null;

    public string? IpAddress => null;

    public string? UserAgent => null;

    public string? Origem => "Testes";

    public string? Job => null;

    public TipoAtor TipoAtor => TipoAtor.Usuario;
}
