using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Infrastructure.Observabilidade;

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Configuracoes;

// SAÚDE DOS WORKERS — no banco de CATÁLOGO (dado operacional da plataforma, não de contribuinte). Uma linha por
// nome técnico; só contadores, timestamps e códigos de erro (nunca PII/payload/segredo — regras 7, 8).

internal sealed class ConfiguracaoSaudeWorkerPersistida : IEntityTypeConfiguration<SaudeWorkerPersistida>
{
    public void Configure(EntityTypeBuilder<SaudeWorkerPersistida> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("saude_worker");
        builder.HasKey(x => x.Worker);
        builder.Property(x => x.Worker).HasMaxLength(64);
        builder.Property(x => x.UltimoErroCodigo).HasMaxLength(128);
    }
}
