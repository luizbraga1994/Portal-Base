using PortalBase.Application.Abstracoes;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Dobras;

internal sealed class RepositorioTenantFake : IRepositorioTenant
{
    private readonly bool _existe;
    private readonly bool _nomeExiste;

    public RepositorioTenantFake(bool existe = false, bool nomeExiste = false)
    {
        _existe = existe;
        _nomeExiste = nomeExiste;
    }

    public List<Tenant> Adicionados { get; } = [];

    public ValueTask<bool> ExisteComCnpjAsync(Cnpj cnpj, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_existe);

    public ValueTask<bool> ExisteComNomeAsync(NomeTenant nome, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_nomeExiste);

    public ValueTask<Tenant?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Adicionados.Find(tenant => tenant.Id == id));

    public ValueTask AdicionarAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        Adicionados.Add(tenant);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Guid>> ListarProvisionaveisAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<Guid>>(Adicionados.ConvertAll(tenant => tenant.Id));
}

internal sealed class UnitOfWorkFake : IUnitOfWork
{
    public bool LancarUnicidade { get; set; }

    public int Salvamentos { get; private set; }

    public Task<int> SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
    {
        Salvamentos++;
        if (LancarUnicidade)
        {
            throw new ViolacaoUnicidadeException();
        }

        return Task.FromResult(1);
    }

    public Task ExecutarEmTransacaoAsync(Func<CancellationToken, Task> operacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operacao);
        return operacao(cancellationToken);
    }
}
