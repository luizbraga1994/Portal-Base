using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Arquivos;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Ingestao;

/// <summary>
/// Prova REAL (MySQL) de que os metadados do arquivo vão para o BANCO DO TENANT, de que a
/// idempotência por hash é tenant-scoped (índice único → conflito) e de que os registros de um
/// contribuinte NÃO aparecem no de outro (isolamento — Prompt 08, seções 17-19). Pulado sem
/// <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class IngestaoTenantMySqlTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly FixtureMultiTenant _fixture;

    public IngestaoTenantMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    private static ArquivoImportacao NovoArquivo(string hash, Guid usuario)
    {
        var arquivo = ArquivoImportacao.Registrar(
            "planilha.csv", "seguro.csv", "csv", "text/csv", 42, hash, usuario, "corr-it", 5, Momento, Momento.AddYears(5));
        arquivo.RegistrarScan(ResultadoScan.Limpo, Momento);
        arquivo.MarcarArmazenado("tenant/seguro.csv", Momento);
        return arquivo;
    }

    private async Task PersistirAsync(string usuario, Guid tenant, ArquivoImportacao arquivo)
    {
        using var escopo = _fixture.CriarEscopo();
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();
        await resolvedor.ResolverAsync(usuario, tenant);
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioArquivoImportacao>();
        await repositorio.AdicionarAsync(arquivo);
        await repositorio.SalvarAsync();
    }

    private async Task<bool> ExisteAsync(string usuario, Guid tenant, string hash)
    {
        using var escopo = _fixture.CriarEscopo();
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();
        await resolvedor.ResolverAsync(usuario, tenant);
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioArquivoImportacao>();
        return await repositorio.ExisteComHashAsync(hash);
    }

    [SkippableFact]
    public async Task Metadado_Vai_Para_O_Banco_Do_Tenant_E_Nao_Vaza_Para_Outro()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, b);

        var hash = new string('a', 64);
        await PersistirAsync(usuario, a, NovoArquivo(hash, Guid.Parse(usuario)));

        (await ExisteAsync(usuario, a, hash)).Should().BeTrue();  // gravado no tenant A
        (await ExisteAsync(usuario, b, hash)).Should().BeFalse(); // isolamento: ausente em B
    }

    [SkippableFact]
    public async Task Hash_Duplicado_No_Mesmo_Tenant_Viola_Unicidade()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a);

        var hash = new string('b', 64);
        await PersistirAsync(usuario, a, NovoArquivo(hash, Guid.Parse(usuario)));

        var acao = async () => await PersistirAsync(usuario, a, NovoArquivo(hash, Guid.Parse(usuario)));

        await acao.Should().ThrowAsync<ViolacaoUnicidadeException>(); // idempotência tenant-scoped no banco
    }

    [SkippableFact]
    public async Task Mesmo_Hash_Em_Tenants_Distintos_E_Permitido()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, b);

        var hash = new string('c', 64);
        await PersistirAsync(usuario, a, NovoArquivo(hash, Guid.Parse(usuario)));

        // O mesmo hash em OUTRO tenant não conflita (conjuntos independentes por base).
        var acao = async () => await PersistirAsync(usuario, b, NovoArquivo(hash, Guid.Parse(usuario)));
        await acao.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task Reivindicacao_Do_Processamento_E_Otimista()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a);
        var hash = new string('d', 64);
        var original = NovoArquivo(hash, Guid.Parse(usuario));
        await PersistirAsync(usuario, a, original);

        // O reenvio localiza o arquivo existente pelo hash (idempotência do envio).
        using (var escopo = _fixture.CriarEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>().ResolverAsync(usuario, a);
            var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioArquivoImportacao>();
            (await repositorio.ObterIdPorHashAsync(hash)).Should().Be(original.Id);
            (await repositorio.ObterIdPorHashAsync(new string('e', 64))).Should().BeNull();
        }

        // Dois processamentos simultâneos do MESMO arquivo: só um vence a reivindicação.
        using var escopo1 = _fixture.CriarEscopo();
        using var escopo2 = _fixture.CriarEscopo();
        await escopo1.ServiceProvider.GetRequiredService<IResolvedorTenant>().ResolverAsync(usuario, a);
        await escopo2.ServiceProvider.GetRequiredService<IResolvedorTenant>().ResolverAsync(usuario, a);
        var repo1 = escopo1.ServiceProvider.GetRequiredService<IRepositorioArquivoImportacao>();
        var repo2 = escopo2.ServiceProvider.GetRequiredService<IRepositorioArquivoImportacao>();
        var copia1 = (await repo1.ObterPorIdAsync(original.Id))!;
        var copia2 = (await repo2.ObterPorIdAsync(original.Id))!;
        copia1.IniciarProcessamento(antivirusObrigatorio: true, Momento.AddMinutes(1));
        copia2.IniciarProcessamento(antivirusObrigatorio: true, Momento.AddMinutes(1));

        (await repo1.TentarSalvarAsync()).Should().BeTrue();
        (await repo2.TentarSalvarAsync()).Should().BeFalse("o outro processamento já reivindicou o arquivo");
        copia2.Estado.Should().Be(EstadoArquivo.Processando, "o estado REAL foi recarregado");

    }
}
