using Microsoft.EntityFrameworkCore;
using PortalBase.Domain.Arquivos;

namespace PortalBase.Infrastructure.Persistencia.BaseTenant;

/// <summary>
/// DbContext da BASE INDIVIDUAL do tenant (um banco por tenant). Conhece
/// SOMENTE as entidades do tenant — NUNCA as do catálogo (seção 16). Não é registrado
/// para abertura automática: o roteamento/resolução por requisição é do Prompt 03
/// (fail-closed: sem tenant resolvido, este contexto não deve abrir conexão).
/// </summary>
public sealed class PortalBaseTenantDbContext : DbContext
{
    private readonly ProtecaoOperacaoEmVoo? _protecao;

    public PortalBaseTenantDbContext(DbContextOptions<PortalBaseTenantDbContext> options)
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

    // ===== Dados de NEGÓCIO do tenant =====
    // Acrescente aqui os DbSets do domínio do seu portal (uma base por tenant: nada daqui vai para o catálogo).

    /// <summary>Arquivos enviados pelo tenant (metadados, hash e resultado do antivírus; o conteúdo fica no armazenamento).</summary>
    public DbSet<ArquivoImportacao> ArquivosImportacao => Set<ArquivoImportacao>();

    /// <summary>Marca visual do CONTRIBUINTE (logo exibida na barra superior) — linha única, no banco do tenant.</summary>
    public DbSet<PortalBase.Domain.Marca.MarcaVisual> MarcaVisual => Set<PortalBase.Domain.Marca.MarcaVisual>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Aplica SOMENTE as configurações da base do tenant — nunca as do catálogo.
        var namespaceConfiguracoes = typeof(PortalBaseTenantDbContext).Namespace + ".Configuracoes";
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(PortalBaseTenantDbContext).Assembly,
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
        // DateOnly (datas de validade dos eventos DeRE) — provider Oracle não materializa nativamente.
        configurationBuilder.Properties<DateOnly>().HaveConversion<ConversorDateOnly>().HaveColumnType("date");

        base.ConfigureConventions(configurationBuilder);
    }
}
