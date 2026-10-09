using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tests.Dobras;

namespace PortalBase.Application.Tests.Auditoria;

/// <summary>Consulta de auditoria autorizada (seções 16, 17, 20, 24): permissão, isolamento, auditar o acesso.</summary>
public sealed class ConsultaAuditoriaAutorizadaTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid Usuario = Guid.NewGuid();

    private readonly ContextoSeguranca _seguranca = new();
    private readonly AutorizacaoTenantFakeIdentidade _tenant = new();
    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly ConsultaAuditoriaFake _consulta = new();
    private readonly ConsultaNomesAtoresFake _nomes = new();
    private readonly AuditoriaFake _auditoria = new();
    private readonly ConsultaAuditoriaAutorizada _servico;

    public ConsultaAuditoriaAutorizadaTests()
    {
        var autorizacao = new ServicoAutorizacao(_seguranca, _tenant, _usuarios, _auditoria, new RelogioFixo(Momento));
        _servico = new ConsultaAuditoriaAutorizada(autorizacao, _consulta, _nomes, _seguranca, _auditoria, new RelogioFixo(Momento));
    }

    private static FiltroAuditoria FiltroBase => new() { Tenant = "IGNORADO" };

    private void AutorizarAuditoria(Guid tenant)
    {
        _seguranca.Definir(Usuario.ToString(), mfaSatisfeito: false);
        _tenant.Permitidos.Add((Usuario.ToString(), tenant));
        _usuarios.DefinirPermissoes(Usuario, tenant, Permissoes.AuditoriaVisualizar);
    }

    [Fact] // §17 — sem a permissão (ex.: Leitor) a consulta é negada
    public async Task Sem_Permissao_De_Auditoria_E_Negado()
    {
        _seguranca.Definir(Usuario.ToString(), mfaSatisfeito: false);
        _tenant.Permitidos.Add((Usuario.ToString(), TenantA));
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.DadosVisualizar); // sem auditoria.visualizar

        var acao = async () => await _servico.ConsultarAsync(TenantA, FiltroBase);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact] // §16/§20 — o tenant do filtro é FORÇADO ao autorizado (nunca vem do cliente)
    public async Task Tenant_Do_Filtro_E_Forcado_Ao_Autorizado()
    {
        AutorizarAuditoria(TenantA);

        await _servico.ConsultarAsync(TenantA, FiltroBase with { Tenant = "tentativa-de-bypass" });

        _consulta.UltimoFiltro!.Tenant.Should().Be(TenantA.ToString());
    }

    [Fact] // §35 — autorizado só para A não consulta B (manipular tenantId é negado)
    public async Task Autorizado_Para_A_Nao_Consulta_B()
    {
        AutorizarAuditoria(TenantA);

        var acao = async () => await _servico.ConsultarAsync(TenantB, FiltroBase);
        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
    }

    [Fact] // §24 — o acesso à auditoria é auditado (evento AuditQueried), sem recursão
    public async Task Consulta_Autorizada_Gera_Evento_AuditQueried()
    {
        AutorizarAuditoria(TenantA);

        await _servico.ConsultarAsync(TenantA, FiltroBase);

        _auditoria.Eventos.Should().Contain(e => e.Operacao == AcaoAuditoria.AuditQueried && e.Tenant == TenantA.ToString());
    }

    [Fact] // Exibição: o ator usuário aparece pelo NOME e login (catálogo); sistema/rotina continuam como gravados
    public async Task Ator_Usuario_Vem_Com_Nome_E_Login_E_Os_Demais_Ficam_Como_Gravados()
    {
        AutorizarAuditoria(TenantA);
        var maria = Guid.NewGuid().ToString();
        _nomes.Usuarios[maria] = new IdentificacaoAtor("Maria Souza", "maria.souza");
        _consulta.Itens.Add(Registro(1, maria, TipoAtor.Usuario));
        _consulta.Itens.Add(Registro(2, "worker-outbox", TipoAtor.JobBackground));
        _consulta.Itens.Add(Registro(3, maria.ToUpperInvariant(), TipoAtor.Usuario));

        var pagina = await _servico.ConsultarAsync(TenantA, FiltroBase);

        pagina.Itens[0].NomeAtor.Should().Be("Maria Souza");
        pagina.Itens[0].LoginAtor.Should().Be("maria.souza");
        pagina.Itens[0].PrincipalId.Should().Be(maria, "o identificador gravado continua sendo a referência da trilha");
        pagina.Itens[1].NomeAtor.Should().BeNull("um ator que não é usuário não é procurado no catálogo");
        pagina.Itens[2].NomeAtor.Should().Be("Maria Souza");
        _nomes.Pedidos.Should().ContainSingle().Which.Should().BeEquivalentTo([maria], "só os usuários da página, sem repetir");
    }

    [Fact] // Filtro: "Ator" aceita nome/login; a lista de atores é SEMPRE do servidor (a do cliente é descartada)
    public async Task Filtro_Ator_Por_Nome_Vira_Os_Ids_Do_Catalogo_E_Descarta_A_Lista_Do_Cliente()
    {
        AutorizarAuditoria(TenantA);
        var maria = Guid.NewGuid().ToString();
        _nomes.Usuarios[maria] = new IdentificacaoAtor("Maria Souza", "maria.souza");

        await _servico.ConsultarAsync(TenantA, FiltroBase with { Ator = "  maria ", Atores = ["id-injetado-pelo-cliente"] });

        _consulta.UltimoFiltro!.Ator.Should().Be("maria");
        _consulta.UltimoFiltro.Atores.Should().BeEquivalentTo(["maria", maria]);

        await _servico.ConsultarAsync(TenantA, FiltroBase with { Atores = ["id-injetado-pelo-cliente"] });
        _consulta.UltimoFiltro!.Atores.Should().BeNull("sem texto no filtro, nenhum ator é imposto pelo cliente");
    }

    private static RegistroAuditoriaDto Registro(long id, string principal, TipoAtor tipo) => new(
        id, TenantA.ToString(), principal, tipo, "ResolverTenant", null, null, "Permitido", null, null, null, null, null, Momento);
}
