using PortalBase.Application.Multitenancy;
using PortalBase.Application.Provisionamento;

namespace PortalBase.Application.Tests.Dobras;

internal sealed class TenantStoreFake : ITenantStore
{
    private readonly TenantMetadados? _metadados;

    public TenantStoreFake(TenantMetadados? metadados) => _metadados = metadados;

    public ValueTask<TenantMetadados?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_metadados);
}

internal sealed class AutorizacaoTenantFake : IAutorizacaoTenant
{
    private readonly bool _autorizado;

    public AutorizacaoTenantFake(bool autorizado) => _autorizado = autorizado;

    public int Chamadas { get; private set; }

    public ValueTask<bool> PodeAcessarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        return ValueTask.FromResult(_autorizado);
    }

    public ValueTask<bool> PodeAdministrarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        return ValueTask.FromResult(_autorizado);
    }
}

internal sealed class AdministradorBancoFake : IAdministradorBancoTenant
{
    public bool BancoExiste { get; set; }

    public int CriacoesDeBanco { get; private set; }

    public int Grants { get; private set; }

    public ValueTask<bool> BancoExisteAsync(string nomeBase, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(BancoExiste);

    public ValueTask CriarBancoSeAusenteAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        CriacoesDeBanco++;
        BancoExiste = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ConcederAcessoRuntimeAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        Grants++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoverBancoAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        BancoExiste = false;
        Proprietario = ProprietarioBaseTenant.BaseVazia;
        return ValueTask.CompletedTask;
    }

    /// <summary>Marca de proprietário da base (o que o MySQL devolveria); vazia por padrão.</summary>
    public ProprietarioBaseTenant Proprietario { get; set; } = ProprietarioBaseTenant.BaseVazia;

    public int RegistrosDeProprietario { get; private set; }

    public ValueTask<ProprietarioBaseTenant> ObterProprietarioAsync(string nomeBase, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Proprietario);

    public ValueTask RegistrarProprietarioAsync(string nomeBase, Guid tenantId, string cnpj, CancellationToken cancellationToken = default)
    {
        RegistrosDeProprietario++;
        Proprietario = new ProprietarioBaseTenant(false, tenantId, cnpj);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MigradorFake : IMigradorTenant
{
    public bool FalharMigracao { get; set; }

    public bool ConectividadeOk { get; set; } = true;

    public int Migracoes { get; private set; }

    public ValueTask<ResultadoMigracao> AplicarMigrationsAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        Migracoes++;
        if (FalharMigracao)
        {
            throw new InvalidOperationException("Falha simulada de migration.");
        }

        return ValueTask.FromResult(new ResultadoMigracao("20260903_TenantBaseInicial", "20260903_TenantBaseInicial"));
    }

    public ValueTask<bool> PodeConectarAsync(string nomeBase, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ConectividadeOk);
}

internal sealed class LockProvisionamentoFake : ILockProvisionamento
{
    private readonly bool _adquire;

    public LockProvisionamentoFake(bool adquire) => _adquire = adquire;

    public ValueTask<IAsyncDisposable?> TentarAdquirirAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IAsyncDisposable?>(_adquire ? new Trava() : null);

    private sealed class Trava : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Validação pós-provisionamento configurável (válida por padrão).</summary>
internal sealed class ValidadorProvisionamentoFake : IValidadorProvisionamento
{
    public bool Valido { get; set; } = true;

    public int Chamadas { get; private set; }

    public ValueTask<ResultadoValidacaoProvisionamento> ValidarAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        return ValueTask.FromResult(Valido
            ? ResultadoValidacaoProvisionamento.Valido
            : ResultadoValidacaoProvisionamento.Invalido("infraestrutura_invalida"));
    }
}

/// <summary>Métricas de provisionamento no-op para os testes.</summary>
internal sealed class MetricasProvisionamentoFake : IMetricasProvisionamento
{
    public int Inicios { get; private set; }

    public int Sucessos { get; private set; }

    public int Falhas { get; private set; }

    public void RegistrarInicio() => Inicios++;

    public void RegistrarSucesso(TimeSpan duracao) => Sucessos++;

    public void RegistrarFalha(TimeSpan duracao) => Falhas++;

    public void RegistrarDuracaoMigracao(TimeSpan duracao)
    {
    }
}
