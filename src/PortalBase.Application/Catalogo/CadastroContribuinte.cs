using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Casos;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Catalogo;

/// <summary>Um contribuinte na lista da plataforma (dados cadastrais públicos — nunca base física ou segredo).</summary>
public sealed record ResumoContribuinte(
    Guid Id,
    string Nome,
    string Cnpj,
    string RazaoSocial,
    EstadoProvisionamento Estado,
    bool ComFalha,
    DateTimeOffset CriadoEmUtc,
    string? UltimaMigracao = null);

/// <summary>Leitura da lista de contribuintes do catálogo (a autorização é feita por <see cref="CadastroContribuinte"/>).</summary>
public interface IConsultaContribuintesPlataforma
{
    Task<IReadOnlyList<ResumoContribuinte>> ListarAsync(int maximo, CancellationToken cancellationToken = default);
}

/// <summary>Dados do novo contribuinte, como vieram da tela (validados no domínio).</summary>
public sealed record NovoContribuinte(
    string? Cnpj, string? Nome, string? RazaoSocial);

/// <summary>Em que etapa o cadastro parou (para a tela explicar o que fazer).</summary>
public enum EtapaCadastroContribuinte
{
    Dados = 0,
    BaseDeDados = 1,
    Ativacao = 2,
    Concluido = 3,
}

/// <summary>Desfecho do cadastro: o contribuinte criado (quando passou da etapa de dados) e a mensagem de negócio.</summary>
public sealed record ResultadoCadastroContribuinte(
    bool Sucesso, EtapaCadastroContribuinte Etapa, Guid? TenantId, string? Nome, EstadoProvisionamento? Estado, string? Erro);

/// <summary>
/// Cadastro de um NOVO contribuinte pela tela da plataforma (Administração → Contribuintes): cria o tenant no catálogo
/// (<see cref="CriarTenantManipulador"/>), prepara a base de dados dele (<see cref="IProvisionadorTenant"/>: cria, aplica
/// as migrations, concede o acesso de runtime e valida) e o ATIVA (<see cref="CicloVidaTenant"/>). Só quem tem a
/// permissão de plataforma <see cref="Permissoes.PlataformaTenantProvisionar"/> (Administrador da plataforma) — decidido
/// no servidor, antes de qualquer gravação. Cada etapa é idempotente e auditada pelos próprios casos de uso; se a base
/// falhar, o contribuinte fica "Falhado" e <see cref="RetomarAsync"/> tenta de novo (nada é refeito duas vezes).
/// </summary>
public sealed class CadastroContribuinte
{
    /// <summary>
    /// Referência (informativa) da credencial de runtime: a conexão real é montada no servidor a partir de
    /// <c>BancoTenant:*</c> — nunca de valor da tela.
    /// </summary>
    public const string ReferenciaConexaoPadrao = "config://BancoTenant";

    /// <summary>Teto da lista na tela.</summary>
    public const int MaximoLista = 1000;

    private readonly IAutorizacaoPlataforma _plataforma;
    private readonly IContextoSeguranca _seguranca;
    private readonly IManipuladorComando<CriarTenantComando, Guid> _criar;
    private readonly IProvisionadorTenant _provisionador;
    private readonly CicloVidaTenant _ciclo;
    private readonly IRepositorioTenant _repositorio;
    private readonly IConsultaContribuintesPlataforma _consulta;

    public CadastroContribuinte(
        IAutorizacaoPlataforma plataforma,
        IContextoSeguranca seguranca,
        IManipuladorComando<CriarTenantComando, Guid> criar,
        IProvisionadorTenant provisionador,
        CicloVidaTenant ciclo,
        IRepositorioTenant repositorio,
        IConsultaContribuintesPlataforma consulta)
    {
        ArgumentNullException.ThrowIfNull(plataforma);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(criar);
        ArgumentNullException.ThrowIfNull(provisionador);
        ArgumentNullException.ThrowIfNull(ciclo);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(consulta);

        _plataforma = plataforma;
        _seguranca = seguranca;
        _criar = criar;
        _provisionador = provisionador;
        _ciclo = ciclo;
        _repositorio = repositorio;
        _consulta = consulta;
    }

    /// <summary>Todos os contribuintes do catálogo (só para a plataforma).</summary>
    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<IReadOnlyList<ResumoContribuinte>> ListarAsync(CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaTenantProvisionar, cancellationToken).ConfigureAwait(false);
        return await _consulta.ListarAsync(MaximoLista, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cria, prepara a base e ativa o contribuinte.</summary>
    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<ResultadoCadastroContribuinte> CriarAsync(NovoContribuinte dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);
        await _plataforma.ExigirAsync(Permissoes.PlataformaTenantProvisionar, cancellationToken).ConfigureAwait(false);
        var solicitante = _seguranca.PrincipalId ?? throw new NaoAutenticadoException();

        var criado = await _criar.ManipularAsync(
            new CriarTenantComando(
                solicitante,
                dados.Cnpj ?? string.Empty,
                dados.Nome ?? string.Empty,
                dados.RazaoSocial ?? string.Empty,
                ReferenciaConexaoPadrao),
            cancellationToken).ConfigureAwait(false);

        if (criado.Falhou)
        {
            return new ResultadoCadastroContribuinte(false, EtapaCadastroContribuinte.Dados, null, null, null, MensagemDados(criado.Erro));
        }

        return await PrepararEAtivarAsync(criado.Valor, solicitante, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tenta de novo preparar a base e ativar um contribuinte que parou no meio (Pendente, Falhado ou Pronto).</summary>
    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<ResultadoCadastroContribuinte> RetomarAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaTenantProvisionar, cancellationToken).ConfigureAwait(false);
        var solicitante = _seguranca.PrincipalId ?? throw new NaoAutenticadoException();

        var tenant = await _repositorio.ObterPorIdAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return new ResultadoCadastroContribuinte(false, EtapaCadastroContribuinte.Dados, null, null, null, "Contribuinte não encontrado. Atualize a lista.");
        }

        if (tenant.Estado is EstadoProvisionamento.Ativo or EstadoProvisionamento.Desabilitado)
        {
            return new ResultadoCadastroContribuinte(true, EtapaCadastroContribuinte.Concluido, tenant.Id, tenant.Nome, tenant.Estado, null);
        }

        return await PrepararEAtivarAsync(tenant.Id, solicitante, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResultadoCadastroContribuinte> PrepararEAtivarAsync(Guid tenantId, string solicitante, CancellationToken cancellationToken)
    {
        var provisionado = await _provisionador.ProvisionarAsync(tenantId, solicitante, cancellationToken).ConfigureAwait(false);
        var tenant = await _repositorio.ObterPorIdAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var nome = tenant?.Nome;
        if (!provisionado.Sucesso)
        {
            // O detalhe técnico fica registrado (sanitizado) no cadastro do contribuinte e na auditoria — não vai para a tela.
            return new ResultadoCadastroContribuinte(false, EtapaCadastroContribuinte.BaseDeDados, tenantId, nome, provisionado.Estado,
                "O contribuinte foi cadastrado, mas a base de dados dele não pôde ser preparada. Confira a credencial administrativa do banco (BancoTenant) e use \"Tentar de novo\".");
        }

        var ativado = await _ciclo.AtivarAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (ativado.Motivo is not null)
        {
            return new ResultadoCadastroContribuinte(false, EtapaCadastroContribuinte.Ativacao, tenantId, nome, ativado.Estado,
                "A base de dados foi preparada, mas o contribuinte não pôde ser ativado. Use \"Tentar de novo\".");
        }

        return new ResultadoCadastroContribuinte(true, EtapaCadastroContribuinte.Concluido, tenantId, nome, ativado.Estado, null);
    }

    // As mensagens do domínio citam a propriedade; a tela mostra o nome do campo.
    private static string NomesDosCampos(string mensagem) => mensagem
        .Replace("'RazaoSocial'", "Razão social", StringComparison.Ordinal);

    private static string MensagemDados(PortalBase.Shared.Resultados.Erro? erro) => erro?.Codigo switch
    {
        "tenant.cnpj_duplicado" => "Já existe um contribuinte com este CNPJ.",
        "tenant.nome_duplicado" or "tenant.duplicado" => erro.Mensagem.Replace("tenant", "contribuinte", StringComparison.Ordinal),
        "tenant.dados_invalidos" => $"Dados inválidos: {NomesDosCampos(erro.Mensagem)}",
        "tenant.nao_autorizado" or "tenant.nao_autenticado" => "Operação de plataforma não autorizada.",
        _ => "Não foi possível cadastrar o contribuinte.",
    };
}
