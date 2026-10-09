using FluentAssertions;
using PortalBase.Application.Administracao;
using PortalBase.Application.Consultas;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tests.Dobras;

namespace PortalBase.Application.Tests.Administracao;

/// <summary>
/// Read-model AUTORIZADO de USUÁRIOS por tenant (Prompt 72). Garante o essencial de segurança:
/// tenant resolvido do CONTEXTO (nunca do cliente), autorização administrativa fail-closed (lança se
/// negada), normalização anti-abuso da paginação, e projeção SOMENTE de campos não secretos (o DTO não
/// tem hash de senha, segredo de MFA nem stamps — garantia de compilação verificada por reflexão).
/// </summary>
public sealed class ConsultaUsuariosAutorizadaTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ---- ListarAsync ----

    [Fact]
    public async Task Listar_SemTenant_RetornaVazio_SemAutorizarNemConsultar()
    {
        var autz = new ServicoAutorizacaoFake();
        var porta = new ConsultaUsuariosAdminFake();
        var consulta = new ConsultaUsuariosAutorizada(new ContextoTenantFake { TenantId = null }, autz, porta);

        var r = await consulta.ListarAsync(new FiltroUsuariosAdmin());

        r.Itens.Should().BeEmpty();
        r.TotalItens.Should().Be(0);
        autz.Chamadas.Should().Be(0);
        porta.ChamadasListar.Should().Be(0);
    }

    [Fact]
    public async Task Listar_NaoAutorizado_Propaga_FailClosed_SemConsultar()
    {
        var porta = new ConsultaUsuariosAdminFake();
        var consulta = new ConsultaUsuariosAutorizada(new ContextoTenantFake { TenantId = Tenant }, new ServicoAutorizacaoFake { Permitir = false }, porta);

        var acao = () => consulta.ListarAsync(new FiltroUsuariosAdmin());

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        porta.ChamadasListar.Should().Be(0); // fail-closed: não tocou o dado
    }

    [Fact]
    public async Task Listar_Autorizado_UsaTenantDoContexto_E_NormalizaPaginacao()
    {
        var porta = new ConsultaUsuariosAdminFake();
        var consulta = new ConsultaUsuariosAutorizada(new ContextoTenantFake { TenantId = Tenant }, new ServicoAutorizacaoFake(), porta);

        // Página/tamanho abusivos vindos "do cliente": o servidor normaliza (1..100).
        await consulta.ListarAsync(new FiltroUsuariosAdmin { Pagina = 0, TamanhoPagina = 10_000 });

        porta.TenantRecebido.Should().Be(Tenant); // NUNCA um valor do cliente — sempre o do contexto
        porta.FiltroRecebido!.Pagina.Should().Be(1);
        porta.FiltroRecebido!.TamanhoPagina.Should().BeLessThanOrEqualTo(100);
    }

    // ---- ObterAsync ----

    [Fact]
    public async Task Obter_SemTenant_RetornaNull_SemAutorizar()
    {
        var autz = new ServicoAutorizacaoFake();
        var consulta = new ConsultaUsuariosAutorizada(new ContextoTenantFake { TenantId = null }, autz, new ConsultaUsuariosAdminFake());

        (await consulta.ObterAsync(Guid.NewGuid())).Should().BeNull();
        autz.Chamadas.Should().Be(0);
    }

    [Fact]
    public async Task Obter_NaoAutorizado_Propaga_FailClosed()
    {
        var consulta = new ConsultaUsuariosAutorizada(new ContextoTenantFake { TenantId = Tenant }, new ServicoAutorizacaoFake { Permitir = false }, new ConsultaUsuariosAdminFake());
        await FluentActions.Awaiting(() => consulta.ObterAsync(Guid.NewGuid())).Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact]
    public async Task Obter_Autorizado_UsaTenantDoContexto()
    {
        var alvo = Guid.NewGuid();
        var porta = new ConsultaUsuariosAdminFake();
        var consulta = new ConsultaUsuariosAutorizada(new ContextoTenantFake { TenantId = Tenant }, new ServicoAutorizacaoFake(), porta);

        await consulta.ObterAsync(alvo);

        porta.TenantRecebido.Should().Be(Tenant);
        porta.UsuarioRecebido.Should().Be(alvo);
    }

    // ---- Normalização e projeção segura ----

    [Fact]
    public void Filtro_Normalizado_Limita_Pagina_Tamanho_E_Busca()
    {
        var f = new FiltroUsuariosAdmin { Pagina = -5, TamanhoPagina = 99_999, Busca = "  " + new string('x', 500) + "  " }.Normalizado();

        f.Pagina.Should().Be(1);
        f.TamanhoPagina.Should().BeLessThanOrEqualTo(100);
        f.Busca!.Length.Should().BeLessThanOrEqualTo(128);
    }

    [Fact]
    public void UsuarioAdministracaoDto_NaoExpoe_Nenhum_Segredo()
    {
        var proibidos = new[] { "HashSenha", "SegredoMfaProtegido", "SecurityStamp", "ConcurrencyStamp", "Senha", "Salt" };
        foreach (var nome in proibidos)
        {
            typeof(UsuarioAdministracaoDto).GetProperty(nome).Should().BeNull($"'{nome}' nunca pode ser projetado no read-model administrativo");
        }
    }

    // ---- Dobra da porta de consulta (Infra) — registra o que recebeu ----

    private sealed class ConsultaUsuariosAdminFake : IConsultaUsuariosAdmin
    {
        public int ChamadasListar { get; private set; }

        public Guid? TenantRecebido { get; private set; }

        public Guid? UsuarioRecebido { get; private set; }

        public FiltroUsuariosAdmin? FiltroRecebido { get; private set; }

        public Task<ResultadoPaginado<UsuarioAdministracaoDto>> ListarPorTenantAsync(Guid tenantId, FiltroUsuariosAdmin filtro, CancellationToken cancellationToken = default)
        {
            ChamadasListar++;
            TenantRecebido = tenantId;
            FiltroRecebido = filtro;
            return Task.FromResult(ResultadoPaginado.Vazio<UsuarioAdministracaoDto>(filtro.Pagina, filtro.TamanhoPagina));
        }

        public Task<UsuarioAdministracaoDto?> ObterDoTenantAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default)
        {
            TenantRecebido = tenantId;
            UsuarioRecebido = usuarioId;
            return Task.FromResult<UsuarioAdministracaoDto?>(null);
        }
    }
}
