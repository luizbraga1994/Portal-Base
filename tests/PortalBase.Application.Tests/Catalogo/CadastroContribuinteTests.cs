using FluentAssertions;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Provisionamento;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Catalogo;

/// <summary>
/// Cadastro de contribuinte pela tela da plataforma: só o Administrador da plataforma; cria → prepara a base → ativa;
/// dados inválidos ou duplicados param antes da base; falha na base deixa o contribuinte "Falhado" e "Tentar de novo"
/// retoma de onde parou.
/// </summary>
public sealed class CadastroContribuinteTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string Cnpj = "11.222.333/0001-81";

    [Fact]
    public async Task Cria_Prepara_A_Base_E_Ativa()
    {
        var c = new Cenario();

        var r = await c.Comando.CriarAsync(new NovoContribuinte(Cnpj, "stake", "Stake Ltda"));

        r.Sucesso.Should().BeTrue();
        r.Etapa.Should().Be(EtapaCadastroContribuinte.Concluido);
        r.Nome.Should().Be("STAKE");
        r.Estado.Should().Be(EstadoProvisionamento.Ativo);
        var tenant = c.Tenants.Adicionados.Should().ContainSingle().Subject;
        tenant.Cnpj.Valor.Should().Be("11222333000181");
        tenant.RazaoSocial.Should().Be("Stake Ltda");
        c.Provisionador.Chamadas.Should().Be(1);
        c.Provisionador.Solicitante.Should().Be("plataforma-1");
        c.Auditoria.Eventos.Select(e => e.Operacao).Should().Contain(["TenantCriado", "TenantActivated"]);
    }

    [Fact]
    public async Task Sem_Permissao_De_Plataforma_Nega_Antes_De_Gravar()
    {
        var c = new Cenario();
        c.Plataforma.Permitir = false;

        var acao = () => c.Comando.CriarAsync(new NovoContribuinte(Cnpj, "STAKE", "Stake Ltda"));

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        c.Tenants.Adicionados.Should().BeEmpty();
        c.Provisionador.Chamadas.Should().Be(0);
    }

    [Theory]
    [InlineData("11.222.333/0001-00", "STAKE", "Stake Ltda", "Dados inválidos")]
    [InlineData(Cnpj, "1ABC", "Stake Ltda", "Dados inválidos")]
    [InlineData(Cnpj, "STAKE", "", "Razão social")]
    public async Task Dados_Invalidos_Param_Antes_Da_Base(string cnpj, string nome, string razao, string trecho)
    {
        var c = new Cenario();

        var r = await c.Comando.CriarAsync(new NovoContribuinte(cnpj, nome, razao));

        r.Sucesso.Should().BeFalse();
        r.Etapa.Should().Be(EtapaCadastroContribuinte.Dados);
        r.Erro.Should().Contain(trecho);
        c.Provisionador.Chamadas.Should().Be(0);
    }

    [Fact]
    public async Task Cnpj_Duplicado_E_Recusado()
    {
        var c = new Cenario(cnpjExiste: true);

        var r = await c.Comando.CriarAsync(new NovoContribuinte(Cnpj, "STAKE", "Stake Ltda"));

        r.Erro.Should().Be("Já existe um contribuinte com este CNPJ.");
        c.Provisionador.Chamadas.Should().Be(0);
    }

    [Fact]
    public async Task Falha_Na_Base_Deixa_Falhado_Sem_Detalhe_Tecnico_E_Tentar_De_Novo_Conclui()
    {
        var c = new Cenario();
        c.Provisionador.Falhar = true;

        var r = await c.Comando.CriarAsync(new NovoContribuinte(Cnpj, "STAKE", "Stake Ltda"));

        r.Sucesso.Should().BeFalse();
        r.Etapa.Should().Be(EtapaCadastroContribuinte.BaseDeDados);
        r.Estado.Should().Be(EstadoProvisionamento.Falhado);
        r.Erro.Should().Contain("Tentar de novo").And.NotContain("MySql");

        c.Provisionador.Falhar = false;
        var retomado = await c.Comando.RetomarAsync(r.TenantId!.Value);

        retomado.Sucesso.Should().BeTrue();
        retomado.Estado.Should().Be(EstadoProvisionamento.Ativo);
        c.Provisionador.Chamadas.Should().Be(2);
    }

    [Fact]
    public async Task Retomar_Um_Ativo_Nao_Refaz_Nada()
    {
        var c = new Cenario();
        var r = await c.Comando.CriarAsync(new NovoContribuinte(Cnpj, "STAKE", "Stake Ltda"));

        var de_novo = await c.Comando.RetomarAsync(r.TenantId!.Value);

        de_novo.Sucesso.Should().BeTrue();
        c.Provisionador.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task Listar_Exige_A_Permissao_De_Plataforma()
    {
        var c = new Cenario();
        c.Plataforma.Permitir = false;

        var acao = () => c.Comando.ListarAsync();

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    private sealed class Cenario
    {
        public Cenario(bool cnpjExiste = false)
        {
            Tenants = new RepositorioTenantFake(existe: cnpjExiste);
            var relogio = new RelogioFixo(Agora);
            var seguranca = new ContextoSegurancaFake { PrincipalId = "plataforma-1" };
            var criar = new CriarTenantManipulador(Plataforma, Tenants, Uow, Auditoria, relogio);
            var ciclo = new CicloVidaTenant(Tenants, Uow, new ServicoAutorizacaoFake(), new ValidadorOk(), Auditoria, seguranca, relogio);
            Provisionador = new ProvisionadorFake(Tenants);
            Comando = new CadastroContribuinte(Plataforma, seguranca, criar, Provisionador, ciclo, Tenants, new ConsultaVazia());
        }

        public AutorizacaoPlataformaFake Plataforma { get; } = new();

        public RepositorioTenantFake Tenants { get; }

        public UnitOfWorkFake Uow { get; } = new();

        public AuditoriaFake Auditoria { get; } = new();

        public ProvisionadorFake Provisionador { get; }

        public CadastroContribuinte Comando { get; }
    }

    private sealed class ProvisionadorFake(RepositorioTenantFake tenants) : IProvisionadorTenant
    {
        public bool Falhar { get; set; }

        public int Chamadas { get; private set; }

        public string? Solicitante { get; private set; }

        public ValueTask<ResultadoProvisionamento> ProvisionarAsync(Guid tenantId, string solicitanteId, CancellationToken cancellationToken = default)
        {
            Chamadas++;
            Solicitante = solicitanteId;
            var tenant = tenants.Adicionados.Single(t => t.Id == tenantId);
            tenant.IniciarProvisionamento(Agora);
            if (Falhar)
            {
                tenant.FalharProvisionamento("MySqlException: Access denied", Agora);
                return ValueTask.FromResult(new ResultadoProvisionamento(tenantId, tenant.Estado, null, "MySqlException: Access denied"));
            }

            tenant.ConcluirProvisionamento("20260918214026_TenantMarcaVisual", "20260918214026_TenantMarcaVisual", Agora);
            return ValueTask.FromResult(new ResultadoProvisionamento(tenantId, tenant.Estado, tenant.SchemaVersion, null));
        }
    }

    private sealed class ValidadorOk : IValidadorProvisionamento
    {
        public ValueTask<ResultadoValidacaoProvisionamento> ValidarAsync(string nomeBase, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ResultadoValidacaoProvisionamento.Valido);
    }

    private sealed class ConsultaVazia : IConsultaContribuintesPlataforma
    {
        public Task<IReadOnlyList<ResumoContribuinte>> ListarAsync(int maximo, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ResumoContribuinte>>([]);
    }
}
