using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Configuracoes;

// Incidente de segurança + timeline (Prompt 30, seções 45-47) — no banco de CATÁLOGO (governança de plataforma,
// append-only). Guarda descrições sanitizadas + HASH/REFERÊNCIA das evidências; nunca payload/PII bruta (§47).

internal sealed class ConfiguracaoIncidenteSeguranca : IEntityTypeConfiguration<IncidenteSeguranca>
{
    public void Configure(EntityTypeBuilder<IncidenteSeguranca> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("incidente_seguranca");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Codigo).HasMaxLength(32).IsRequired();
        builder.Property(x => x.TenantAfetadoId);
        builder.Property(x => x.Tipo).HasConversion<int>().IsRequired();
        builder.Property(x => x.Severidade).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Titulo).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Descricao).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.SistemasAfetados).HasMaxLength(500);
        builder.Property(x => x.CategoriasDados).HasMaxLength(300);
        builder.Property(x => x.ClassificacaoRisco).HasConversion<int>().IsRequired();
        builder.Property(x => x.JustificativaClassificacao).HasMaxLength(2000);
        builder.Property(x => x.ResponsavelId).HasMaxLength(64);
        builder.Property(x => x.AbertoPorId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.HashEvidencias).HasMaxLength(64);
        builder.Property(x => x.ReferenciaEvidencias).HasMaxLength(256);
        builder.Property(x => x.ResumoEncerramento).HasMaxLength(2000);
        builder.Property(x => x.DetectadoEmUtc).IsRequired();
        builder.Property(x => x.AtualizadoEmUtc).IsRequired();
        builder.Property(x => x.EncerradoEmUtc);
        builder.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => x.Codigo).IsUnique().HasDatabaseName("ux_incidente_codigo");
        builder.HasIndex(x => new { x.Status, x.DetectadoEmUtc }).HasDatabaseName("ix_incidente_status");
        builder.HasIndex(x => x.TenantAfetadoId).HasDatabaseName("ix_incidente_tenant");

        builder.HasMany(typeof(EventoTimelineIncidente), nameof(IncidenteSeguranca.Timeline))
            .WithOne()
            .HasForeignKey(nameof(EventoTimelineIncidente.IncidenteId))
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(nameof(IncidenteSeguranca.Timeline)).HasField("_timeline").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ConfiguracaoEventoTimelineIncidente : IEntityTypeConfiguration<EventoTimelineIncidente>
{
    public void Configure(EntityTypeBuilder<EventoTimelineIncidente> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("incidente_timeline");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.IncidenteId).IsRequired();
        builder.Property(x => x.Sequencia).IsRequired();
        builder.Property(x => x.Tipo).HasConversion<int>().IsRequired();
        builder.Property(x => x.AtorId).HasMaxLength(64);
        builder.Property(x => x.Descricao).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.OcorridoEmUtc).IsRequired();

        builder.HasIndex(x => new { x.IncidenteId, x.Sequencia }).IsUnique().HasDatabaseName("ux_incidente_timeline_seq");
    }
}
