using Microsoft.EntityFrameworkCore;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Infrastructure.Persistencia.Catalogo;

/// <summary>
/// DbContext do banco de CATÁLOGO CENTRAL (<c>portalbase_catalogo</c>). Conhece
/// SOMENTE as entidades do catálogo (tenants, usuários, papéis, permissões, vínculos e
/// auditoria administrativa). NUNCA conhece entidades contábeis do contribuinte
/// (seção 16). Convenções: InnoDB (padrão do provider), utf8mb4/collation
/// case-sensitive, DECIMAL(19,2) e timestamps em UTC.
/// </summary>
public sealed class PortalBaseCatalogoDbContext : DbContext
{
    private readonly ProtecaoOperacaoEmVoo? _protecao;

    public PortalBaseCatalogoDbContext(DbContextOptions<PortalBaseCatalogoDbContext> options)
        : base(options)
    {
        _protecao = ProtecaoOperacaoEmVoo.Localizar(options);

        // P81-R1 (F81-14): todo SaveChanges é ATÔMICO. No modo padrão (WhenNeeded) o EF não abre transação para um
        // lote único; com o provider MySQL o lote (UPDATE … WHERE ConcurrencyStamp; INSERT do histórico) roda em
        // autocommit e o INSERT da operação perdedora ficava confirmado antes da DbUpdateConcurrencyException.
        // Com transação ativa (explícita), o EF a reutiliza — não há aninhamento.
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
    }

    /// <summary>
    /// P62-R1: aguarda comandos/leitores EM VOO antes de fechar a conexão — o fim do escopo (circuito/requisição) não pode
    /// devolver ao pool um canal no meio de uma leitura (ver <see cref="ProtecaoOperacaoEmVoo"/>).
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (_protecao is not null)
        {
            await _protecao.AguardarOciosoAsync(this).ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc cref="DisposeAsync"/>
    public override void Dispose()
    {
        _protecao?.AguardarOcioso(this);
        base.Dispose();
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Usuario> Usuarios => Set<Usuario>();

    public DbSet<Papel> Papeis => Set<Papel>();

    public DbSet<Permissao> Permissoes => Set<Permissao>();

    public DbSet<PapelPermissao> PapelPermissoes => Set<PapelPermissao>();

    public DbSet<UsuarioTenant> UsuarioTenants => Set<UsuarioTenant>();

    public DbSet<UsuarioTenantPapel> UsuarioTenantPapeis => Set<UsuarioTenantPapel>();

    public DbSet<TokenRecuperacaoConta> TokensRecuperacao => Set<TokenRecuperacaoConta>();

    public DbSet<CodigoRecuperacaoMfa> CodigosRecuperacaoMfa => Set<CodigoRecuperacaoMfa>();

    public DbSet<RegistroAuditoria> RegistrosAuditoria => Set<RegistroAuditoria>();

    // Governança de incidentes de segurança (Prompt 30) — plataforma, append-only.
    public DbSet<PortalBase.Domain.Incidentes.IncidenteSeguranca> IncidentesSeguranca => Set<PortalBase.Domain.Incidentes.IncidenteSeguranca>();

    public DbSet<PortalBase.Domain.Incidentes.EventoTimelineIncidente> IncidentesTimeline => Set<PortalBase.Domain.Incidentes.EventoTimelineIncidente>();

    // Catálogo de backups (Prompt 50) — metadados de backup/restore/DR de plataforma. Nunca segredos.
    public DbSet<PortalBase.Domain.Backup.RegistroBackup> RegistrosBackup => Set<PortalBase.Domain.Backup.RegistroBackup>();

    /// <summary>Marca visual GLOBAL do portal (logo padrão exibida no login e como fallback) — linha única.</summary>
    public DbSet<PortalBase.Domain.Marca.MarcaVisual> MarcaVisualPlataforma => Set<PortalBase.Domain.Marca.MarcaVisual>();

    /// <summary>Configuração do e-mail (SMTP) definida pela tela da plataforma — linha única; a senha fica no cofre.</summary>
    public DbSet<PortalBase.Domain.Notificacoes.ConfiguracaoEmail> ConfiguracoesEmail => Set<PortalBase.Domain.Notificacoes.ConfiguracaoEmail>();

    /// <summary>Saúde dos workers gravada pelo host do Worker (lida pela Web em Operações).</summary>
    public DbSet<PortalBase.Infrastructure.Observabilidade.SaudeWorkerPersistida> SaudeWorkers => Set<PortalBase.Infrastructure.Observabilidade.SaudeWorkerPersistida>();

    /// <summary>Tarefas em segundo plano (ARQ-006): fila durável do processamento pesado tirado do circuito.</summary>
    public DbSet<PortalBase.Domain.Tarefas.TarefaSegundoPlano> TarefasSegundoPlano => Set<PortalBase.Domain.Tarefas.TarefaSegundoPlano>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Aplica SOMENTE as configurações do catálogo — nunca as do tenant (seção 16).
        var namespaceConfiguracoes = typeof(PortalBaseCatalogoDbContext).Namespace + ".Configuracoes";
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(PortalBaseCatalogoDbContext).Assembly,
            tipo => tipo.Namespace == namespaceConfiguracoes);

        modelBuilder.UseCollation("utf8mb4_0900_as_cs");

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<decimal>().HavePrecision(19, 2);
        configurationBuilder.Properties<DateTime>().HaveConversion<ConversorDateTimeUtc>();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<ConversorDateTimeOffsetUtc>();

        base.ConfigureConventions(configurationBuilder);
    }
}
