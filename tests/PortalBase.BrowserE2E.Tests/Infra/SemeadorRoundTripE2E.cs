using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Identidade;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Arquivos;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>Dados semeados (só teste) para o round-trip de UM contribuinte — todos rastreáveis pelo teste.</summary>
internal sealed record ContribuinteE2E(
    Guid TenantId,
    string Nome,
    string Cnpj,
    string RazaoSocial,
    Guid UsuarioId,
    string Login,
    string Senha,
    string MfaSegredoBase32,
    Guid ArquivoSementeId,
    string NomeArquivoSemente)
{
    /// <summary>Credencial do usuário administrador do contribuinte.</summary>
    public CredencialE2E Credencial => new(UsuarioId, Login, Senha, MfaSegredoBase32);

    /// <summary>E-mail do usuário: é o identificador digitado no login.</summary>
    public string Email => SemeadorRoundTripE2E.EmailDe(Login);
}

/// <summary>Cenário isolado por execução: contribuinte A (round-trip) + contribuinte B (isolamento).</summary>
internal sealed record CenarioRoundTrip(ContribuinteE2E A, ContribuinteE2E B);

/// <summary>Credencial REAL de um usuário semeado (senha + segredo TOTP) — usada pelo Browser no fluxo de login.</summary>
internal sealed record CredencialE2E(Guid UsuarioId, string Login, string Senha, string MfaSegredoBase32)
{
    /// <summary>Códigos de RECUPERAÇÃO devolvidos pela ativação real do MFA (uso único cada).</summary>
    public IReadOnlyList<string> CodigosRecuperacao { get; init; } = [];

    /// <summary>E-mail do usuário: é o identificador digitado no login.</summary>
    public string Email => SemeadorRoundTripE2E.EmailDe(Login);
}

/// <summary>
/// Prompt 76 — SEED EXCLUSIVO do ambiente E2E integrado (nunca de produção). Sobre o MESMO MySQL real do
/// harness, provisiona tenants A/B (base física + Ativo), cria um usuário com credencial + MFA REAL (via
/// <see cref="GestaoMfa"/>, guardando o segredo Base32 que o serviço devolve para o Browser calcular o
/// step-up) + papéis Administrador e Analista — e semeia UM arquivo (metadados) na base de cada tenant, o dado que
/// as telas mostram para provar sessão, isolamento e restauração. Determinístico e isolado por execução; sem bypass
/// de autenticação/autorização; sem dado fictício na camada de produção.
/// </summary>
internal static class SemeadorRoundTripE2E
{
    private static readonly int[] PesosPrimeiro = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] PesosSegundo = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    private static readonly PapelConhecido[] PapeisNecessarios =
        [PapelConhecido.Administrador, PapelConhecido.Analista]; // o Administrador já tem tudo; o Analista prova a combinação

    /// <summary>E-mail semeado para o usuário (o identificador do login), já na forma canônica.</summary>
    public static string EmailDe(string login) => Usuario.NormalizarEmail($"{login}@exemplo.com");

    public static async Task<CenarioRoundTrip> SemearAsync(AmbienteE2EIntegrado ambiente)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        var a = await SemearContribuinteAsync(ambiente).ConfigureAwait(false);
        var b = await SemearContribuinteAsync(ambiente).ConfigureAwait(false);
        return new CenarioRoundTrip(a, b);
    }

    private static async Task<ContribuinteE2E> SemearContribuinteAsync(AmbienteE2EIntegrado ambiente)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var cnpj = GerarCnpjValido();
        var nome = $"E2E_{sufixo.ToUpperInvariant()}"; // nome ESCOLHIDO -> base portalbase_tenant_e2e_<sufixo>
        var razao = $"Contribuinte E2E {sufixo}";
        var login = $"u_{sufixo}";
        var senha = $"Senha!{sufixo}9A";

        var tenantId = await ProvisionarTenantAtivoAsync(ambiente, cnpj, nome, razao).ConfigureAwait(false);
        var usuarioId = await CriarUsuarioComCredencialAsync(ambiente, login, senha).ConfigureAwait(false);
        var (segredoMfa, _) = await AtivarMfaAsync(ambiente, usuarioId).ConfigureAwait(false);
        await VincularComPapeisAsync(ambiente, usuarioId, tenantId, PapeisNecessarios).ConfigureAwait(false);

        var nomeArquivo = $"semente-{sufixo}.csv";
        var arquivoId = await SemearArquivoAsync(ambiente, usuarioId, tenantId, nomeArquivo).ConfigureAwait(false);

        return new ContribuinteE2E(tenantId, nome, cnpj, razao, usuarioId, login, senha, segredoMfa, arquivoId, nomeArquivo);
    }

    /// <summary>
    /// P62-R1 — semeia um usuário ADICIONAL no tenant informado, com credencial + MFA reais e os papéis pedidos
    /// (ex.: Leitor para RBAC negado). Mesmo caminho do usuário principal; nenhum bypass.
    /// </summary>
    public static async Task<CredencialE2E> SemearUsuarioAsync(AmbienteE2EIntegrado ambiente, Guid tenantId, params PapelConhecido[] papeis)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        ArgumentNullException.ThrowIfNull(papeis);
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var login = $"u_{sufixo}";
        var senha = $"Senha!{sufixo}9A";
        var usuarioId = await CriarUsuarioComCredencialAsync(ambiente, login, senha).ConfigureAwait(false);
        var (segredoMfa, codigosRecuperacao) = await AtivarMfaAsync(ambiente, usuarioId).ConfigureAwait(false);
        await VincularComPapeisAsync(ambiente, usuarioId, tenantId, papeis).ConfigureAwait(false);
        return new CredencialE2E(usuarioId, login, senha, segredoMfa) { CodigosRecuperacao = codigosRecuperacao };
    }

    /// <summary>
    /// Semeia um usuário SEM MFA cadastrado, vinculado ao <paramref name="tenantId"/> com os <paramref name="papeis"/>:
    /// entra só com a senha (sessão sem o segundo fator). Serve para provar que ação de alto impacto exige o MFA NO
    /// SERVIDOR — o caminho legítimo até uma sessão sem "amr=mfa" depois da correção do SEC-001.
    /// </summary>
    public static async Task<CredencialE2E> SemearUsuarioSemMfaAsync(AmbienteE2EIntegrado ambiente, Guid tenantId, params PapelConhecido[] papeis)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        ArgumentNullException.ThrowIfNull(papeis);
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var login = $"s_{sufixo}";
        var senha = $"Senha!{sufixo}9A";
        var usuarioId = await CriarUsuarioComCredencialAsync(ambiente, login, senha).ConfigureAwait(false);
        await VincularComPapeisAsync(ambiente, usuarioId, tenantId, papeis).ConfigureAwait(false);
        return new CredencialE2E(usuarioId, login, senha, MfaSegredoBase32: string.Empty);
    }

    /// <summary>
    /// Semeia um usuário com o perfil global "Administrador da plataforma": credencial real, SEM MFA e SEM vínculo com
    /// contribuinte nenhum — o acesso a qualquer contribuinte e a dispensa do segundo fator vêm do perfil.
    /// </summary>
    public static async Task<CredencialE2E> SemearAdministradorPlataformaAsync(AmbienteE2EIntegrado ambiente)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var login = $"plat_{sufixo}";
        var senha = $"Senha!{sufixo}9A";
        var usuarioId = await CriarUsuarioComCredencialAsync(ambiente, login, senha).ConfigureAwait(false);
        using var escopo = ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var usuario = await catalogo.Usuarios.FirstAsync(u => u.Id == usuarioId).ConfigureAwait(false);
        usuario.DefinirAdministradorPlataforma(true, relogio.AgoraUtc);
        await catalogo.SaveChangesAsync().ConfigureAwait(false);
        return new CredencialE2E(usuarioId, login, senha, MfaSegredoBase32: string.Empty);
    }

    /// <summary>Vincula um usuário JÁ semeado a mais um contribuinte (mesmo caminho de semeadura; nenhum bypass).</summary>
    public static Task VincularAsync(AmbienteE2EIntegrado ambiente, Guid usuarioId, Guid tenantId, params PapelConhecido[] papeis)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        ArgumentNullException.ThrowIfNull(papeis);
        return VincularComPapeisAsync(ambiente, usuarioId, tenantId, papeis);
    }

    /// <summary>
    /// P62-R1 — grava eventos na trilha de auditoria do tenant pelo serviço REAL (<see cref="IAuditoria"/>:
    /// encadeamento de integridade, sanitização, append-only). Base de dados real para provar paginação/filtros/
    /// ordenação server-side na tela de auditoria — sem dado de demonstração.
    /// </summary>
    public static async Task SemearEventosAuditoriaAsync(
        AmbienteE2EIntegrado ambiente, Guid tenantId, string ator, IEnumerable<(string Acao, ResultadoAuditoria Resultado, DateTimeOffset Quando)> eventos)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        ArgumentNullException.ThrowIfNull(eventos);
        using var escopo = ambiente.CriarEscopoSuporte();
        var auditoria = escopo.ServiceProvider.GetRequiredService<IAuditoria>();
        foreach (var (acao, resultado, quando) in eventos)
        {
            await auditoria.RegistrarAsync(new EventoAuditoria(ator, tenantId.ToString(), acao, resultado, quando)
            {
                TipoRecurso = "E2E",
                RecursoId = "paginacao",
            }).ConfigureAwait(false);
        }
    }

    private static async Task<Guid> ProvisionarTenantAtivoAsync(AmbienteE2EIntegrado ambiente, string cnpj, string nome, string razao)
    {
        Guid tenantId;
        using (var escopo = ambiente.CriarEscopoSuporte())
        {
            var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioTenant>();
            var uow = escopo.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
            var tenant = Tenant.Criar(Cnpj.Criar(cnpj), NomeTenant.Criar(nome), razao, "cofre://conexao", relogio.AgoraUtc);
            tenantId = tenant.Id;
            await repositorio.AdicionarAsync(tenant).ConfigureAwait(false);
            await uow.SalvarAlteracoesAsync().ConfigureAwait(false);
        }

        ambiente.RegistrarTenant(tenantId); // expurgo da base física no fim

        using (var escopo = ambiente.CriarEscopoSuporte())
        {
            var provisionador = escopo.ServiceProvider.GetRequiredService<IProvisionadorTenant>();
            var resultado = await provisionador.ProvisionarAsync(tenantId, "admin-sistema").ConfigureAwait(false);
            if (!resultado.Sucesso)
            {
                throw new InvalidOperationException($"Falha ao provisionar tenant E2E: {resultado.Erro}");
            }
        }

        using (var escopo = ambiente.CriarEscopoSuporte())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
            var tenant = await catalogo.Tenants.FirstAsync(t => t.Id == tenantId).ConfigureAwait(false);
            tenant.Ativar(relogio.AgoraUtc); // Pronto → Ativo (operacional: o Worker só processa tenants operacionais)
            await catalogo.SaveChangesAsync().ConfigureAwait(false);
        }

        return tenantId;
    }

    private static async Task<Guid> CriarUsuarioComCredencialAsync(AmbienteE2EIntegrado ambiente, string login, string senha)
    {
        using var escopo = ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<IHasherSenha>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var usuario = Usuario.Criar(login, "Usuário E2E", EmailDe(login), relogio.AgoraUtc);
        usuario.DefinirCredencial(hasher.Gerar(senha), relogio.AgoraUtc); // hash real (nunca senha em claro)
        catalogo.Usuarios.Add(usuario);
        await catalogo.SaveChangesAsync().ConfigureAwait(false);
        return usuario.Id;
    }

    // Ativa MFA pelo FLUXO REAL (GestaoMfa) e devolve o segredo Base32 que o serviço gerou — o Browser
    // calcula o código do momento (como um autenticador) — e os códigos de recuperação exibidos ao usuário.
    // O servidor valida o segundo fator normalmente.
    private static async Task<(string Segredo, IReadOnlyList<string> CodigosRecuperacao)> AtivarMfaAsync(AmbienteE2EIntegrado ambiente, Guid usuarioId)
    {
        using var escopo = ambiente.CriarEscopoSuporte();
        var mfa = escopo.ServiceProvider.GetRequiredService<GestaoMfa>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();

        var provisionamento = await mfa.IniciarAtivacaoAsync(usuarioId).ConfigureAwait(false);
        var codigo = CodigoTotpTeste.Gerar(provisionamento.SegredoBase32, relogio.AgoraUtc);
        var concluida = await mfa.ConfirmarAtivacaoAsync(usuarioId, codigo).ConfigureAwait(false);
        return (provisionamento.SegredoBase32, concluida.CodigosRecuperacao);
    }

    private static async Task VincularComPapeisAsync(AmbienteE2EIntegrado ambiente, Guid usuarioId, Guid tenantId, PapelConhecido[] papeis)
    {
        using var escopo = ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();

        var vinculo = UsuarioTenant.Conceder(usuarioId, tenantId, relogio.AgoraUtc);
        catalogo.UsuarioTenants.Add(vinculo);
        await catalogo.SaveChangesAsync().ConfigureAwait(false);

        foreach (var papel in papeis)
        {
            var papelEntidade = await catalogo.Papeis.FirstAsync(p => p.Nome == papel.ToString()).ConfigureAwait(false);
            catalogo.UsuarioTenantPapeis.Add(new UsuarioTenantPapel(vinculo.Id, papelEntidade.Id));
        }

        await catalogo.SaveChangesAsync().ConfigureAwait(false);
    }

    // Semeia UM arquivo (metadados, scan limpo, armazenado) na base física do tenant, pelo caminho de domínio real.
    private static async Task<Guid> SemearArquivoAsync(AmbienteE2EIntegrado ambiente, Guid usuarioId, Guid tenantId, string nomeArquivo)
    {
        using var escopo = ambiente.CriarEscopoSuporte();
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();
        var fabrica = escopo.ServiceProvider.GetRequiredService<IFabricaContextoTenant>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var agora = relogio.AgoraUtc;

        var contextoTenant = await resolvedor.ResolverAsync(usuarioId.ToString(), tenantId).ConfigureAwait(false);
        await using var db = await fabrica.CriarAsync(contextoTenant).ConfigureAwait(false);

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nomeArquivo)));
        var arquivo = ArquivoImportacao.Registrar(nomeArquivo, $"{Guid.NewGuid():N}.csv", "csv", "text/csv", 42, hash, usuarioId, "e2e-semente", 5, agora, agora.AddYears(5));
        arquivo.RegistrarScan(ResultadoScan.Limpo, agora);
        arquivo.MarcarArmazenado($"{tenantId:N}/{arquivo.Id:N}.csv", agora);

        db.ArquivosImportacao.Add(arquivo);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return arquivo.Id;
    }

    private static string GerarCnpjValido()
    {
        var digitos = new int[14];
        for (var i = 0; i < 12; i++)
        {
            digitos[i] = Random.Shared.Next(0, 10);
        }

        if (digitos.Take(12).Distinct().Count() == 1)
        {
            digitos[0] = (digitos[0] + 1) % 10;
        }

        digitos[12] = CalcularDigito(digitos, PesosPrimeiro);
        digitos[13] = CalcularDigito(digitos, PesosSegundo);
        return string.Concat(digitos.Select(d => (char)('0' + d)));
    }

    private static int CalcularDigito(int[] digitos, int[] pesos)
    {
        var soma = 0;
        for (var i = 0; i < pesos.Length; i++)
        {
            soma += digitos[i] * pesos[i];
        }

        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }
}
