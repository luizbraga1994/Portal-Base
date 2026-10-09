using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Provisionamento;

/// <summary>Resultado da aplicação de migrations em uma base de tenant.</summary>
public sealed record ResultadoMigracao(string SchemaVersion, string UltimaMigracao);

/// <summary>Resultado de um provisionamento/atualização de tenant.</summary>
public sealed record ResultadoProvisionamento(Guid TenantId, EstadoProvisionamento Estado, string? SchemaVersion, string? Erro)
{
    // Sucesso = tenant provisionado (Pronto/Ready no provisionamento inicial, ou Ativo/Active
    // preservado numa atualização de schema).
    public bool Sucesso => Estado is EstadoProvisionamento.Pronto or EstadoProvisionamento.Ativo;
}

/// <summary>
/// Quem é o dono de uma base de tenant JÁ existente, segundo a marca gravada nela no provisionamento.
/// <see cref="Vazia"/> = a base não tem nenhuma tabela (nada a vazar). Sem marca, <see cref="TenantId"/>/<see cref="Cnpj"/>
/// são nulos (origem desconhecida).
/// </summary>
public sealed record ProprietarioBaseTenant(bool Vazia, Guid? TenantId, string? Cnpj)
{
    public static readonly ProprietarioBaseTenant BaseVazia = new(true, null, null);
}

/// <summary>
/// Operações administrativas de banco (DDL/grants) — usam a credencial ADMINISTRATIVA,
/// separada da credencial de runtime (seção 19). Todo nome de base é validado e nunca vem
/// do cliente (seções 10, 11).
/// </summary>
public interface IAdministradorBancoTenant
{
    ValueTask<bool> BancoExisteAsync(string nomeBase, CancellationToken cancellationToken = default);

    ValueTask CriarBancoSeAusenteAsync(string nomeBase, CancellationToken cancellationToken = default);

    /// <summary>Concede à credencial de RUNTIME apenas DML na base do tenant (nunca *.*) — seção 18.</summary>
    ValueTask ConcederAcessoRuntimeAsync(string nomeBase, CancellationToken cancellationToken = default);

    /// <summary>Remove a base (uso administrativo/testes). Nome validado.</summary>
    ValueTask RemoverBancoAsync(string nomeBase, CancellationToken cancellationToken = default);

    /// <summary>Lê a marca de proprietário de uma base existente (e se ela está vazia). Nome validado.</summary>
    ValueTask<ProprietarioBaseTenant> ObterProprietarioAsync(string nomeBase, CancellationToken cancellationToken = default);

    /// <summary>Grava (substituindo) a marca de proprietário da base: tenant e CNPJ donos dos dados. Idempotente.</summary>
    ValueTask RegistrarProprietarioAsync(string nomeBase, Guid tenantId, string cnpj, CancellationToken cancellationToken = default);
}

/// <summary>Aplica as migrations do schema do tenant a uma base específica (credencial admin).</summary>
public interface IMigradorTenant
{
    ValueTask<ResultadoMigracao> AplicarMigrationsAsync(string nomeBase, CancellationToken cancellationToken = default);

    ValueTask<bool> PodeConectarAsync(string nomeBase, CancellationToken cancellationToken = default);
}

/// <summary>Resultado da validação PÓS-provisionamento de uma base de tenant (seções 4, 15).</summary>
public sealed record ResultadoValidacaoProvisionamento(bool Ok, string? Motivo)
{
    public static readonly ResultadoValidacaoProvisionamento Valido = new(true, null);

    public static ResultadoValidacaoProvisionamento Invalido(string motivo) => new(false, motivo);
}

/// <summary>
/// Validação PÓS-provisionamento e de REATIVAÇÃO (Prompt 09, seções 4, 15, 22): confirma que a
/// infraestrutura está de fato pronta — base existe, conecta com a credencial de runtime, sem
/// migrations pendentes e com as tabelas essenciais presentes. "O banco existe" não basta.
/// </summary>
public interface IValidadorProvisionamento
{
    ValueTask<ResultadoValidacaoProvisionamento> ValidarAsync(string nomeBase, CancellationToken cancellationToken = default);
}

/// <summary>
/// Métricas de observabilidade do provisionamento (Prompt 09, seção 33): contadores de
/// início/sucesso/falha e duração. NUNCA registra senha, connection string ou segredo.
/// </summary>
public interface IMetricasProvisionamento
{
    void RegistrarInicio();

    void RegistrarSucesso(TimeSpan duracao);

    void RegistrarFalha(TimeSpan duracao);

    void RegistrarDuracaoMigracao(TimeSpan duracao);
}

/// <summary>
/// Lock de exclusão mútua de provisionamento por tenant (seção 24). Retorna <c>null</c>
/// se outro processo já detém o lock (não iniciar operação concorrente).
/// </summary>
public interface ILockProvisionamento
{
    ValueTask<IAsyncDisposable?> TentarAdquirirAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Provisiona (ou atualiza o schema de) um tenant. Idempotente e fail-closed.</summary>
public interface IProvisionadorTenant
{
    ValueTask<ResultadoProvisionamento> ProvisionarAsync(Guid tenantId, string solicitanteId, CancellationToken cancellationToken = default);
}

/// <summary>Última migration do schema dos contribuintes que ESTE código conhece (a versão que toda base deve ter).</summary>
public interface IVersaoSchemaTenant
{
    /// <summary>Id da última migration (ex.: <c>20261007180844_TenantDeclaracaoEventoAuxiliar</c>); nulo se não houver.</summary>
    string? UltimaMigracaoDisponivel { get; }
}
