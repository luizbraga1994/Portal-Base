using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.AcessoLocal;

/// <summary>
/// Dados do acesso a criar. Razão social, nome e e-mail só são necessários (e, se faltarem, perguntados) quando o
/// contribuinte/usuário ainda não existe. Nenhum campo é secreto, exceto <see cref="Senha"/> (só por entrada padrão).
/// </summary>
public sealed record EntradaAcessoLocal(string Cnpj, string Login, IReadOnlyList<PapelConhecido> Papeis)
{
    /// <summary>Nome escolhido do tenant (ex.: STAKE → base portalbase_tenant_stake). Exigido só ao criar o contribuinte.</summary>
    public string? Tenant { get; init; }

    public string? RazaoSocial { get; init; }

    public string? Nome { get; init; }

    public string? Email { get; init; }

    /// <summary>Troca a senha de um usuário que já existe (sem isto, a senha existente é mantida).</summary>
    public bool RedefinirSenha { get; init; }

    /// <summary>
    /// Cadastra o MFA (TOTP) no terminal. OPCIONAL (padrão: não): sem MFA o login pede só a senha; as operações de
    /// alto impacto que a política do servidor protege com segundo fator (<c>Permissoes.ExigemMfa</c>) ficam negadas.
    /// </summary>
    public bool AtivarMfa { get; init; }

    /// <summary>Desliga o MFA de um usuário que já o tem (segredo e códigos de recuperação apagados, sessões revogadas).</summary>
    public bool RemoverMfa { get; init; }

    /// <summary>Senha recebida pela entrada padrão (automação). Nula ⇒ perguntada no terminal, sem eco.</summary>
    public string? Senha { get; init; }
}

public enum ResultadoAcessoLocal
{
    Sucesso = 0,
    Falha = 1,
    EntradaInvalida = 2,
    Recusado = 3,
}

/// <summary>
/// Cria (ou completa) o acesso LOCAL de desenvolvimento pelos MESMOS serviços da aplicação: contribuinte no
/// catálogo + provisionamento real da base <c>tenant_*</c> + ativação; usuário com senha (só o HASH do
/// <see cref="IHasherSenha"/>); vínculo e papéis por <see cref="IRepositorioAcesso"/>; MFA por <see cref="GestaoMfa"/>.
/// <para>
/// Duas fases: (1) LEITURA + coleta + validação de tudo — entrada inválida, senha recusada ou contribuinte/vínculo
/// bloqueado terminam aqui SEM gravar nada; (2) gravação. Idempotente: reexecutar não duplica nada e não troca a
/// senha sem <see cref="EntradaAcessoLocal.RedefinirSenha"/>. Cada alteração é auditada com o ator
/// <c>acesso-local:&lt;usuário do sistema&gt;</c>, origem <see cref="OrigemAuditoria"/> e uma correlação por execução
/// (regra 9). Não há principal autenticado para os casos de uso administrativos (é o primeiro acesso): por isso
/// este é um comando de operador, restrito ao desenvolvimento local por <see cref="GuardaAmbienteLocal"/>, e não um
/// atalho dentro da aplicação.
/// </para>
/// </summary>
public sealed partial class CriadorAcessoLocal
{
    /// <summary>Origem gravada em cada evento de auditoria desta ferramenta (distingue de "Web"/"Worker").</summary>
    public const string OrigemAuditoria = "AcessoLocal";

    // Mesmo vocabulário do caso de uso CriarTenant (o evento de criação de tenant não tem constante própria).
    private const string AcaoTenantCriado = "TenantCriado";
    private const string AcaoMfaDesativado = "MfaDesativado";
    private const int TentativasSenha = 3;
    private const int TentativasCodigoMfa = 3;

    // A conexão física da base é montada a partir de BancoTenant + nome da base: a referência abaixo é apenas informativa.
    private const string ReferenciaConexaoLocal = "local://desenvolvimento";

    private readonly IServiceScopeFactory _escopos;
    private readonly ITerminal _terminal;
    private readonly string _ator;

    public CriadorAcessoLocal(IServiceScopeFactory escopos, ITerminal terminal, string ator)
    {
        ArgumentNullException.ThrowIfNull(escopos);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentException.ThrowIfNullOrWhiteSpace(ator);

        _escopos = escopos;
        _terminal = terminal;
        _ator = ator;
    }

    /// <summary>Ator de auditoria a partir do usuário do sistema operacional (só caracteres seguros).</summary>
    public static string AtorPara(string? usuarioSistema)
    {
        var limpo = CaracteresInseguros().Replace(usuarioSistema ?? string.Empty, string.Empty);
        return "acesso-local:" + (limpo.Length == 0 ? "desconhecido" : limpo[..Math.Min(limpo.Length, 64)]);
    }

    public async Task<ResultadoAcessoLocal> ExecutarAsync(EntradaAcessoLocal entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);

        // ---------- Fase 1: validação, leitura e coleta (NADA é gravado) ----------
        var erroBasico = ValidarBasico(entrada, out var cnpj);
        if (erroBasico is not null)
        {
            _terminal.Erro(erroBasico);
            return ResultadoAcessoLocal.EntradaInvalida;
        }

        var login = entrada.Login.Trim();

        await using var escopo = _escopos.CreateAsyncScope();
        var servicos = escopo.ServiceProvider;

        // Todos os eventos desta execução saem com a mesma correlação e a origem da ferramenta (regra 9).
        var correlacao = Guid.NewGuid().ToString("N");
        var contextoAuditoria = servicos.GetRequiredService<IContextoAuditoriaMutavel>();
        contextoAuditoria.DefinirOrigem(OrigemAuditoria, TipoAtor.Usuario);
        contextoAuditoria.DefinirCorrelacao(correlacao, traceId: null);

        var catalogo = servicos.GetRequiredService<PortalBaseCatalogoDbContext>();
        // Base/tabelas do catálogo apagadas: recriadas aqui com a conta migradora (mesma garantia da Web em Development).
        var garantia = await servicos.GetRequiredService<GarantiaCatalogoDesenvolvimento>().GarantirAsync(cancellationToken).ConfigureAwait(false);
        if (garantia.Situacao == SituacaoGarantiaCatalogo.Reparado)
        {
            _terminal.Escrever("[ok] " + garantia.Mensagem);
        }

        var erroCatalogo = await VerificarCatalogoAsync(catalogo, cancellationToken).ConfigureAwait(false);
        if (erroCatalogo is not null)
        {
            _terminal.Erro(erroCatalogo);
            return ResultadoAcessoLocal.Falha;
        }

        var tenant = await ObterTenantAsync(servicos, catalogo, cnpj, cancellationToken).ConfigureAwait(false);
        var usuario = await servicos.GetRequiredService<IRepositorioUsuario>().ObterPorLoginAsync(login, cancellationToken).ConfigureAwait(false);

        if (tenant?.Estado == EstadoProvisionamento.Desabilitado)
        {
            _terminal.Erro("Este contribuinte está DESABILITADO. Reative-o pela administração antes de criar acessos.");
            return ResultadoAcessoLocal.Recusado;
        }

        if (tenant is not null && usuario is not null)
        {
            var vinculo = await servicos.GetRequiredService<IRepositorioAcesso>()
                .ObterVinculoAsync(usuario.Id, tenant.Id, cancellationToken).ConfigureAwait(false);
            if (vinculo is not null && !vinculo.EstaAtivo)
            {
                _terminal.Erro("O vínculo deste usuário com o contribuinte foi REVOGADO. Reconceda-o pela administração.");
                return ResultadoAcessoLocal.Recusado;
            }
        }

        var dados = Coletar(entrada, login, cnpj, tenantNovo: tenant is null, usuarioNovo: usuario is null);
        if (dados is null)
        {
            _terminal.Aviso("nada foi gravado.");
            return ResultadoAcessoLocal.EntradaInvalida;
        }

        // O e-mail é o identificador do LOGIN: um usuário novo não pode usar o e-mail de outro (seria barrado pelo índice
        // único só na gravação), e um usuário que já existe entra com o e-mail que JÁ tem.
        if (!await EmailDeLoginCoerenteAsync(servicos, usuario, login, entrada.Email, dados.Value.Email, cancellationToken).ConfigureAwait(false))
        {
            _terminal.Aviso("nada foi gravado.");
            return ResultadoAcessoLocal.EntradaInvalida;
        }

        // O nome define a base física: nome já usado por outro tenant, ou base que sobrou com dados de OUTRO contribuinte,
        // é recusado ANTES de gravar (o nome é imutável; um tenant criado com ele ficaria preso).
        if (dados.Value.NomeTenant is { } nomeNovo
            && !await NomeDisponivelAsync(servicos, nomeNovo, cnpj, cancellationToken).ConfigureAwait(false))
        {
            _terminal.Aviso("nada foi gravado.");
            return ResultadoAcessoLocal.EntradaInvalida;
        }

        // ---------- Fase 2: gravação ----------
        tenant = await GarantirTenantAsync(servicos, tenant, cnpj, dados.Value, entrada.Tenant, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return ResultadoAcessoLocal.Falha;
        }

        usuario = await GarantirUsuarioAsync(servicos, usuario, login, dados.Value, cancellationToken).ConfigureAwait(false);
        if (usuario is null)
        {
            return ResultadoAcessoLocal.Falha;
        }

        if (!await GarantirVinculoAsync(servicos, usuario, tenant, entrada.Papeis, cancellationToken).ConfigureAwait(false))
        {
            return ResultadoAcessoLocal.Falha;
        }

        var administradorPlataforma = await GarantirAdministradorPlataformaAsync(servicos, usuario, entrada.Papeis, cancellationToken).ConfigureAwait(false);

        var mfaAtivo = entrada.RemoverMfa
            ? await RemoverMfaAsync(servicos, usuario, cancellationToken).ConfigureAwait(false)
            : await GarantirMfaAsync(servicos, usuario, entrada.AtivarMfa, administradorPlataforma, cancellationToken).ConfigureAwait(false);

        _terminal.Escrever(string.Empty);
        _terminal.Escrever("Acesso local pronto:");
        _terminal.Escrever($"  tenant       : {tenant.Nome} — {tenant.RazaoSocial} (CNPJ {tenant.Cnpj.Valor})");
        _terminal.Escrever($"  base         : {tenant.NomeBaseFisica} (id {tenant.Id})");
        _terminal.Escrever($"  login        : {usuario.Email} (o e-mail é o que se digita para entrar)");
        _terminal.Escrever($"  usuário      : {usuario.Login}");
        _terminal.Escrever($"  papéis       : {string.Join(", ", entrada.Papeis.Distinct())}");
        _terminal.Escrever($"  plataforma   : {(administradorPlataforma ? "Administrador da plataforma (acesso total a todos os contribuintes, sem MFA)" : "não")}");
        _terminal.Escrever($"  MFA          : {(mfaAtivo ? "ativo (o login pede o código do app)" : administradorPlataforma ? "não cadastrado (dispensado para o Administrador da plataforma)" : "não cadastrado (opcional; o login pede só a senha)")}");
        _terminal.Escrever($"  auditoria    : ator {_ator}, correlação {correlacao}");
        _terminal.Escrever($"Próximos passos: dotnet run --project src/PortalBase.Web  e entre em http://localhost:5251/conta/entrar com o e-mail {usuario.Email}");

        if (entrada.AtivarMfa && !mfaAtivo)
        {
            _terminal.Aviso("O MFA não foi confirmado. Rode o comando de novo (o restante já está pronto) para tentar outra vez.");
            return ResultadoAcessoLocal.Falha;
        }

        return ResultadoAcessoLocal.Sucesso;
    }

    // ===================== Fase 1 =====================

    private string? ValidarBasico(EntradaAcessoLocal entrada, out Cnpj cnpj)
    {
        cnpj = null!;
        try
        {
            cnpj = Cnpj.Criar(entrada.Cnpj ?? string.Empty);
        }
        catch (EntradaInvalidaException)
        {
            return "CNPJ inválido (confira os 14 dígitos e os dígitos verificadores).";
        }

        if (!LoginValido().IsMatch((entrada.Login ?? string.Empty).Trim()))
        {
            return "Login inválido: use de 3 a 64 caracteres entre letras, dígitos, ponto, hífen, sublinhado e @.";
        }

        if (entrada.AtivarMfa && entrada.RemoverMfa)
        {
            return "Cadastrar e remover o MFA na mesma execução não faz sentido: escolha um.";
        }

        if (entrada.Papeis is null || entrada.Papeis.Count == 0 || entrada.Papeis.Any(p => !Enum.IsDefined(p)))
        {
            return "Informe ao menos um papel válido.";
        }

        if (entrada.AtivarMfa && !_terminal.Interativo)
        {
            return "Cadastrar o MFA exige terminal interativo. Em automação, não use --com-mfa.";
        }

        return null;
    }

    private static async Task<string?> VerificarCatalogoAsync(PortalBaseCatalogoDbContext catalogo, CancellationToken cancellationToken)
    {
        // Depois da garantia: o que restar (sem conta migradora, MySQL fora) é orientado para o script de ambiente.
        if (!await catalogo.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            return "Não foi possível conectar ao catálogo. O MySQL está no ar? Rode antes .\\scripts\\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo.";
        }

        var pendentes = (await catalogo.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count();
        return pendentes == 0
            ? null
            : $"O catálogo tem {pendentes} migration(s) pendente(s). Suba a Web em Development (ela aplica as migrations do catálogo no arranque) "
              + "ou rode .\\scripts\\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo (sem -PularCriacaoBanco).";
    }

    private static async Task<Tenant?> ObterTenantAsync(
        IServiceProvider servicos, PortalBaseCatalogoDbContext catalogo, Cnpj cnpj, CancellationToken cancellationToken)
    {
        var id = await catalogo.Tenants.AsNoTracking()
            .Where(t => t.Cnpj == cnpj)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Carregado pelo repositório (rastreado pelo MESMO contexto que o provisionador usa neste escopo).
        return id is { } existente
            ? await servicos.GetRequiredService<IRepositorioTenant>().ObterPorIdAsync(existente, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private readonly record struct DadosColetados(NomeTenant? NomeTenant, string RazaoSocial, string Nome, string Email, string? Senha);

    /// <summary>Completa (perguntando, se interativo) e valida o que a gravação vai precisar. Nada é gravado aqui.</summary>
    private DadosColetados? Coletar(EntradaAcessoLocal entrada, string login, Cnpj cnpj, bool tenantNovo, bool usuarioNovo)
    {
        var nomeTenant = entrada.Tenant?.Trim() ?? string.Empty;
        var razaoSocial = entrada.RazaoSocial?.Trim() ?? string.Empty;
        var nome = entrada.Nome?.Trim() ?? string.Empty;
        var email = entrada.Email?.Trim() ?? string.Empty;

        if (tenantNovo)
        {
            _terminal.Escrever($"O contribuinte de CNPJ {cnpj.Valor} ainda não existe e será criado.");
            if (!Completar(ref nomeTenant, "Nome do tenant (ex.: STAKE; vira a base portalbase_tenant_<nome>)", "--tenant")
                || !Completar(ref razaoSocial, "Razão social do contribuinte", "--razao-social"))
            {
                return null;
            }
        }

        if (usuarioNovo)
        {
            _terminal.Escrever($"O usuário '{login}' ainda não existe e será criado.");
            if (!Completar(ref nome, "Nome do usuário", "--nome") || !Completar(ref email, "E-mail do usuário", "--email"))
            {
                return null;
            }
        }

        // Os próprios agregados validam nome do tenant, razão social, nome e e-mail (mesmas regras da aplicação); nada é
        // gravado.
        NomeTenant? nomeValidado = null;
        try
        {
            if (tenantNovo)
            {
                nomeValidado = NomeTenant.Criar(nomeTenant);
                _ = Tenant.Criar(cnpj, nomeValidado, razaoSocial, ReferenciaConexaoLocal, DateTimeOffset.UtcNow);
            }

            if (usuarioNovo)
            {
                _ = Usuario.Criar(login, nome, email, DateTimeOffset.UtcNow);
            }
        }
        catch (EntradaInvalidaException excecao)
        {
            _terminal.Erro("Dados inválidos: " + excecao.Message);
            return null;
        }

        string? senha = null;
        if (usuarioNovo || entrada.RedefinirSenha)
        {
            senha = ObterSenha(entrada, login, email);
            if (senha is null)
            {
                return null;
            }
        }

        return new DadosColetados(nomeValidado, razaoSocial, nome, email, senha);
    }

    private async Task<bool> EmailDeLoginCoerenteAsync(
        IServiceProvider servicos, Usuario? usuario, string login, string? emailInformado, string emailColetado, CancellationToken cancellationToken)
    {
        if (usuario is not null)
        {
            if (!string.IsNullOrWhiteSpace(emailInformado)
                && !string.Equals(Usuario.NormalizarEmail(emailInformado), usuario.Email, StringComparison.Ordinal))
            {
                _terminal.Aviso(
                    $"o usuário '{login}' já existe com o e-mail {usuario.Email}: é ESSE e-mail que entra no login " +
                    "(o --email informado não altera o cadastro).");
            }

            return true;
        }

        var dono = await servicos.GetRequiredService<IRepositorioUsuario>().ObterPorEmailAsync(emailColetado, cancellationToken).ConfigureAwait(false);
        if (dono is null)
        {
            return true;
        }

        _terminal.Erro(
            $"O e-mail {dono.Email} já é o login do usuário '{dono.Login}'. Use --login {dono.Login} para completar o acesso " +
            "dele, ou informe outro --email.");
        return false;
    }

    private async Task<bool> NomeDisponivelAsync(IServiceProvider servicos, NomeTenant nome, Cnpj cnpj, CancellationToken cancellationToken)
    {
        if (await servicos.GetRequiredService<IRepositorioTenant>().ExisteComNomeAsync(nome, cancellationToken).ConfigureAwait(false))
        {
            _terminal.Erro($"Já existe um tenant com o nome {nome.Valor} (de outro CNPJ). Escolha outro --tenant.");
            return false;
        }

        var admin = servicos.GetRequiredService<IAdministradorBancoTenant>();
        if (!await admin.BancoExisteAsync(nome.NomeBaseFisica, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // A base sobrou de antes (ex.: catálogo recriado): só é reaproveitada se estiver vazia ou for deste mesmo CNPJ.
        var dono = await admin.ObterProprietarioAsync(nome.NomeBaseFisica, cancellationToken).ConfigureAwait(false);
        if (!ReaproveitamentoBaseTenant.Permitido(dono, tenantId: null, cnpj, nome.NomeBaseFisica))
        {
            _terminal.Erro(
                $"A base {nome.NomeBaseFisica} já existe com dados de outro contribuinte (ou sem dono conhecido) e não será " +
                "herdada. Escolha outro --tenant.");
            return false;
        }

        _terminal.Aviso(dono.Vazia
            ? $"a base {nome.NomeBaseFisica} já existe (vazia) e será reaproveitada."
            : $"a base {nome.NomeBaseFisica} já existe e é deste mesmo CNPJ: será reaproveitada com os dados que tem.");
        return true;
    }

    private bool Completar(ref string valor, string rotulo, string opcao)
    {
        if (valor.Length > 0)
        {
            return true;
        }

        if (!_terminal.Interativo)
        {
            _terminal.Erro($"Informe {opcao} (sem terminal interativo não há como perguntar).");
            return false;
        }

        valor = _terminal.Perguntar(rotulo)?.Trim() ?? string.Empty;
        if (valor.Length == 0)
        {
            _terminal.Erro($"{rotulo}: valor obrigatório.");
            return false;
        }

        return true;
    }

    private string? ObterSenha(EntradaAcessoLocal entrada, string login, string email)
    {
        if (entrada.Senha is not null)
        {
            var erro = PoliticaSenha.Validar(entrada.Senha, login, email);
            if (erro is not null)
            {
                _terminal.Erro(erro);
                return null;
            }

            return entrada.Senha;
        }

        if (!_terminal.Interativo)
        {
            _terminal.Erro("Sem terminal interativo: informe a senha pela entrada padrão (--senha-stdin).");
            return null;
        }

        for (var tentativa = 0; tentativa < TentativasSenha; tentativa++)
        {
            var senha = _terminal.PerguntarOculto($"Senha do usuário '{login}' (mín. {PoliticaSenha.TamanhoMinimo} caracteres; não aparece na tela)");
            var erro = PoliticaSenha.Validar(senha, login, email);
            if (erro is not null)
            {
                _terminal.Aviso(erro);
                continue;
            }

            if (!string.Equals(senha, _terminal.PerguntarOculto("Confirme a senha"), StringComparison.Ordinal))
            {
                _terminal.Aviso("As senhas não conferem.");
                continue;
            }

            return senha;
        }

        _terminal.Erro($"Senha não definida após {TentativasSenha} tentativas.");
        return null;
    }

    // ===================== Fase 2 =====================

    private async Task<Tenant?> GarantirTenantAsync(
        IServiceProvider servicos, Tenant? tenant, Cnpj cnpj, DadosColetados dados, string? nomeInformado, CancellationToken cancellationToken)
    {
        var razaoSocial = dados.RazaoSocial;
        var unidade = servicos.GetRequiredService<IUnitOfWork>();
        var auditoria = servicos.GetRequiredService<IAuditoria>();
        var relogio = servicos.GetRequiredService<IRelogio>();

        if (tenant is not null)
        {
            _terminal.Escrever($"[ok] contribuinte já existe e foi reaproveitado: {tenant.Nome} ({tenant.RazaoSocial})");
            if (!string.IsNullOrWhiteSpace(nomeInformado)
                && !string.Equals(tenant.Nome, nomeInformado.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _terminal.Aviso($"o nome do tenant é fixo depois de criado (define a base física); mantido {tenant.Nome}.");
            }

            if (razaoSocial.Length > 0 && !string.Equals(tenant.RazaoSocial, razaoSocial, StringComparison.Ordinal))
            {
                _terminal.Aviso("a razão social informada difere da cadastrada; a cadastrada foi mantida.");
            }
        }
        else
        {
            tenant = Tenant.Criar(cnpj, dados.NomeTenant!, razaoSocial, ReferenciaConexaoLocal, relogio.AgoraUtc);
            await servicos.GetRequiredService<IRepositorioTenant>().AdicionarAsync(tenant, cancellationToken).ConfigureAwait(false);
            try
            {
                await unidade.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ViolacaoUnicidadeException)
            {
                _terminal.Erro("Outro processo criou um contribuinte com este CNPJ ou nome agora mesmo. Rode o comando de novo.");
                return null;
            }

            await auditoria.RegistrarAsync(
                new EventoAuditoria(_ator, tenant.Id.ToString(), AcaoTenantCriado, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
                {
                    TipoRecurso = "Tenant",
                    RecursoId = tenant.Id.ToString(),
                    Motivo = "Acesso local de desenvolvimento",
                },
                cancellationToken).ConfigureAwait(false);
            _terminal.Escrever($"[ok] tenant criado: {tenant.Nome} ({tenant.RazaoSocial})");
        }

        if (!tenant.EstaProvisionado)
        {
            _terminal.Escrever($"     provisionando a base {tenant.NomeBaseFisica} (cria a base, aplica as migrations e concede acesso)...");
            var resultado = await servicos.GetRequiredService<IProvisionadorTenant>()
                .ProvisionarAsync(tenant.Id, _ator, cancellationToken).ConfigureAwait(false);
            if (!resultado.Sucesso)
            {
                _terminal.Erro($"O provisionamento falhou: {resultado.Erro ?? "motivo não informado"}. Corrija e rode o comando de novo (é idempotente).");
                return null;
            }

            _terminal.Escrever($"[ok] base do contribuinte provisionada (schema {resultado.SchemaVersion})");
        }

        if (!tenant.EstaOperacional)
        {
            tenant.Ativar(relogio.AgoraUtc);
            await unidade.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            await auditoria.RegistrarAsync(
                new EventoAuditoria(_ator, tenant.Id.ToString(), AcaoAuditoria.TenantActivated, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
                {
                    TipoRecurso = "Tenant",
                    RecursoId = tenant.Id.ToString(),
                },
                cancellationToken).ConfigureAwait(false);
            _terminal.Escrever("[ok] contribuinte ativado");
        }

        return tenant;
    }

    private async Task<Usuario?> GarantirUsuarioAsync(
        IServiceProvider servicos, Usuario? usuario, string login, DadosColetados dados, CancellationToken cancellationToken)
    {
        var unidade = servicos.GetRequiredService<IUnitOfWork>();
        var hasher = servicos.GetRequiredService<IHasherSenha>();
        var auditoria = servicos.GetRequiredService<IAuditoria>();
        var relogio = servicos.GetRequiredService<IRelogio>();

        if (usuario is not null && dados.Senha is null)
        {
            _terminal.Escrever($"[ok] usuário '{login}' já existe; a senha foi mantida (use --redefinir-senha para trocá-la)");
            AvisarSeImpedido(usuario, relogio.AgoraUtc);
            return usuario;
        }

        if (usuario is null)
        {
            usuario = Usuario.Criar(login, dados.Nome, dados.Email, relogio.AgoraUtc);
            usuario.DefinirCredencial(hasher.Gerar(dados.Senha!), relogio.AgoraUtc);
            await servicos.GetRequiredService<IRepositorioUsuario>().AdicionarAsync(usuario, cancellationToken).ConfigureAwait(false);
            try
            {
                await unidade.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ViolacaoUnicidadeException)
            {
                _terminal.Erro($"Outro processo criou o login '{login}' (ou um usuário com este e-mail) agora mesmo. Rode o comando de novo.");
                return null;
            }

            await auditoria.RegistrarAsync(
                new EventoAuditoria(_ator, EscoposAuditoria.Identidade, AcaoAuditoria.UserCreated, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
                {
                    TipoRecurso = "Usuario",
                    RecursoId = usuario.Id.ToString(),
                    Motivo = "Acesso local de desenvolvimento",
                },
                cancellationToken).ConfigureAwait(false);
            _terminal.Escrever($"[ok] usuário '{login}' criado (a senha é guardada só como hash)");
            return usuario;
        }

        usuario.DefinirCredencial(hasher.Gerar(dados.Senha!), relogio.AgoraUtc);
        await unidade.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await auditoria.RegistrarAsync(
            new EventoAuditoria(_ator, EscoposAuditoria.Identidade, AcaoAuditoria.PasswordChanged, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
            {
                TipoRecurso = "Usuario",
                RecursoId = usuario.Id.ToString(),
                Motivo = "Senha redefinida pelo comando de acesso local",
            },
            cancellationToken).ConfigureAwait(false);
        _terminal.Escrever($"[ok] senha do usuário '{login}' redefinida");
        AvisarSeImpedido(usuario, relogio.AgoraUtc);
        return usuario;
    }

    private void AvisarSeImpedido(Usuario usuario, DateTimeOffset agoraUtc)
    {
        if (usuario.EstaImpedidoDeAutenticar(agoraUtc))
        {
            _terminal.Aviso($"o usuário está {usuario.Status} ou temporariamente bloqueado; o login vai falhar até a administração liberá-lo.");
        }
    }

    /// <summary>
    /// Quem recebe o papel Administrador pelo acesso local também vira "Administrador da plataforma" (acesso total, todos
    /// os contribuintes, sem MFA) — é quem administra o portal na máquina de desenvolvimento. Idempotente e auditado.
    /// </summary>
    private async Task<bool> GarantirAdministradorPlataformaAsync(
        IServiceProvider servicos, Usuario usuario, IReadOnlyList<PapelConhecido> papeis, CancellationToken cancellationToken)
    {
        if (!papeis.Contains(PapelConhecido.Administrador))
        {
            return usuario.AdministradorPlataforma;
        }

        if (usuario.AdministradorPlataforma)
        {
            _terminal.Escrever("[ok] já é Administrador da plataforma");
            return true;
        }

        var relogio = servicos.GetRequiredService<IRelogio>();
        usuario.DefinirAdministradorPlataforma(true, relogio.AgoraUtc);
        await servicos.GetRequiredService<IUnitOfWork>().SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await servicos.GetRequiredService<IAuditoria>().RegistrarAsync(
            new EventoAuditoria(_ator, EscoposAuditoria.Plataforma, AcaoAuditoria.PlatformAdminGranted, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
            {
                TipoRecurso = "Usuario",
                RecursoId = usuario.Id.ToString(),
                Motivo = "acesso local (Development): Administrador também é Administrador da plataforma",
            },
            cancellationToken).ConfigureAwait(false);
        _terminal.Escrever("[ok] perfil Administrador da plataforma concedido (acesso total a todos os contribuintes, sem MFA)");
        return true;
    }

    private async Task<bool> GarantirVinculoAsync(
        IServiceProvider servicos, Usuario usuario, Tenant tenant, IReadOnlyList<PapelConhecido> papeis, CancellationToken cancellationToken)
    {
        var acesso = servicos.GetRequiredService<IRepositorioAcesso>();
        var unidade = servicos.GetRequiredService<IUnitOfWork>();
        var auditoria = servicos.GetRequiredService<IAuditoria>();
        var relogio = servicos.GetRequiredService<IRelogio>();

        var vinculo = await acesso.ObterVinculoAsync(usuario.Id, tenant.Id, cancellationToken).ConfigureAwait(false);
        if (vinculo is null)
        {
            vinculo = UsuarioTenant.Conceder(usuario.Id, tenant.Id, relogio.AgoraUtc);
            await acesso.AdicionarVinculoAsync(vinculo, cancellationToken).ConfigureAwait(false);
            try
            {
                await unidade.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ViolacaoUnicidadeException)
            {
                _terminal.Erro("Outro processo vinculou este usuário ao contribuinte agora mesmo. Rode o comando de novo.");
                return false;
            }

            await auditoria.RegistrarAsync(
                new EventoAuditoria(_ator, tenant.Id.ToString(), AcaoAuditoria.TenantUserLinked, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
                {
                    TipoRecurso = "Usuario",
                    RecursoId = usuario.Id.ToString(),
                },
                cancellationToken).ConfigureAwait(false);
            _terminal.Escrever("[ok] usuário vinculado ao contribuinte");
        }
        else if (!vinculo.EstaAtivo)
        {
            // Revogado entre a leitura da fase 1 e agora (corrida): não reconcede por conta própria.
            _terminal.Erro("O vínculo deste usuário com o contribuinte foi REVOGADO. Reconceda-o pela administração.");
            return false;
        }

        var concedidos = new List<PapelConhecido>();
        foreach (var papel in papeis.Distinct())
        {
            var entidade = await acesso.ResolverPapelAsync(papel, cancellationToken).ConfigureAwait(false);
            if (entidade is null)
            {
                _terminal.Erro($"O papel '{papel}' não existe no catálogo (migrations do catálogo incompletas?).");
                return false;
            }

            if (!await acesso.PapelDoVinculoExisteAsync(vinculo.Id, entidade.Id, cancellationToken).ConfigureAwait(false))
            {
                await acesso.AdicionarPapelDoVinculoAsync(new UsuarioTenantPapel(vinculo.Id, entidade.Id), cancellationToken).ConfigureAwait(false);
                concedidos.Add(papel);
            }
        }

        if (concedidos.Count == 0)
        {
            _terminal.Escrever("[ok] papéis já concedidos");
            return true;
        }

        try
        {
            await unidade.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            _terminal.Erro("Outro processo concedeu estes papéis agora mesmo. Rode o comando de novo.");
            return false;
        }

        foreach (var papel in concedidos)
        {
            await auditoria.RegistrarAsync(
                new EventoAuditoria(_ator, tenant.Id.ToString(), AcaoAuditoria.RoleAssigned, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
                {
                    TipoRecurso = "Usuario",
                    RecursoId = usuario.Id.ToString(),
                    Motivo = "Papel " + papel,
                },
                cancellationToken).ConfigureAwait(false);
        }

        _terminal.Escrever($"[ok] papéis concedidos: {string.Join(", ", concedidos)}");
        return true;
    }

    private async Task<bool> GarantirMfaAsync(IServiceProvider servicos, Usuario usuario, bool ativar, bool administradorPlataforma, CancellationToken cancellationToken)
    {
        if (usuario.MfaHabilitado)
        {
            _terminal.Escrever("[ok] MFA já está ativo para este usuário");
            return true;
        }

        if (!ativar)
        {
            _terminal.Escrever("[ok] MFA não cadastrado (é opcional; o próprio usuário cadastra em Minha conta → \"Ativar agora\", ou rode de novo com -ComMfa / --com-mfa).");
            if (administradorPlataforma)
            {
                // O Administrador da plataforma tem o segundo fator dispensado pelo servidor em todas as operações.
                _terminal.Escrever("     Como Administrador da plataforma, nenhuma operação pede o segundo fator.");
            }
            else
            {
                _terminal.Escrever("     Sem MFA, a política do servidor nega só as operações de alto impacto (assinar/transmitir");
                _terminal.Escrever("     eventos, fechar/reabrir competência, resolver conciliação).");
            }

            return false;
        }

        // GestaoMfa grava o segredo CIFRADO, audita início/confirmação e gera os códigos de recuperação (no banco, só hash).
        var mfa = servicos.GetRequiredService<GestaoMfa>();
        var provisionamento = await mfa.IniciarAtivacaoAsync(usuario.Id, cancellationToken).ConfigureAwait(false);

        _terminal.Escrever(string.Empty);
        _terminal.Escrever("Cadastre o MFA no seu app autenticador (Microsoft Authenticator, Google Authenticator, 1Password...).");
        _terminal.Escrever("Escolha \"inserir chave manualmente\" e digite a chave abaixo (ela é exibida só agora):");
        _terminal.Escrever("  chave : " + Agrupar(provisionamento.SegredoBase32));
        _terminal.Escrever("  ou URI: " + provisionamento.UriProvisionamento);

        for (var tentativa = 1; tentativa <= TentativasCodigoMfa; tentativa++)
        {
            var codigo = _terminal.Perguntar("Código de 6 dígitos mostrado pelo app")?.Replace(" ", string.Empty, StringComparison.Ordinal);
            try
            {
                var concluida = await mfa.ConfirmarAtivacaoAsync(usuario.Id, codigo ?? string.Empty, cancellationToken).ConfigureAwait(false);
                _terminal.Escrever("[ok] MFA ativado. Códigos de RECUPERAÇÃO (cada um vale uma vez; guarde em local seguro):");
                foreach (var recuperacao in concluida.CodigosRecuperacao)
                {
                    _terminal.Escrever("    " + recuperacao);
                }

                return true;
            }
            catch (MfaInvalidoException)
            {
                _terminal.Aviso($"código inválido ou expirado (tentativa {tentativa} de {TentativasCodigoMfa}). Confira o relógio do celular.");
            }
        }

        return false;
    }

    /// <summary>
    /// Desliga o MFA (<see cref="Usuario.DesabilitarMfa"/>: apaga o segredo e rotaciona o security-stamp, o que encerra
    /// as sessões abertas) e apaga os códigos de recuperação — senão voltariam a valer num recadastro. Auditado.
    /// </summary>
    private async Task<bool> RemoverMfaAsync(IServiceProvider servicos, Usuario usuario, CancellationToken cancellationToken)
    {
        if (!usuario.MfaHabilitado && string.IsNullOrEmpty(usuario.SegredoMfaProtegido))
        {
            _terminal.Escrever("[ok] o usuário já está sem MFA (o login pede só a senha)");
            return false;
        }

        var relogio = servicos.GetRequiredService<IRelogio>();
        usuario.DesabilitarMfa(relogio.AgoraUtc);
        await servicos.GetRequiredService<IUnitOfWork>().SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);

        // Depois de desligar: se esta etapa falhar, os códigos restantes ficam inertes (sem MFA ativo não são aceitos)
        // e rodar de novo termina a limpeza.
        var catalogo = servicos.GetRequiredService<PortalBaseCatalogoDbContext>();
        var removidos = await catalogo.CodigosRecuperacaoMfa
            .Where(c => c.UsuarioId == usuario.Id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await servicos.GetRequiredService<IAuditoria>().RegistrarAsync(
            new EventoAuditoria(_ator, EscoposAuditoria.Identidade, AcaoMfaDesativado, ResultadoAuditoria.Permitido, relogio.AgoraUtc)
            {
                TipoRecurso = "Usuario",
                RecursoId = usuario.Id.ToString(),
                Motivo = "MFA removido pelo comando de acesso local",
            },
            cancellationToken).ConfigureAwait(false);

        _terminal.Escrever($"[ok] MFA removido ({removidos} código(s) de recuperação apagado(s); sessões abertas encerradas)");
        return false;
    }

    private static string Agrupar(string base32)
    {
        var saida = new StringBuilder(base32.Length + (base32.Length / 4));
        for (var i = 0; i < base32.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                saida.Append(' ');
            }

            saida.Append(base32[i]);
        }

        return saida.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9._@\-]{3,64}$")]
    private static partial Regex LoginValido();

    [GeneratedRegex(@"[^A-Za-z0-9._\-]")]
    private static partial Regex CaracteresInseguros();
}
