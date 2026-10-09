using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Notificacoes;

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Configuracoes;

// CONFIGURAÇÃO DO E-MAIL (SMTP) definida pela tela da plataforma — catálogo, linha ÚNICA. Só dados não sensíveis: a
// senha do SMTP fica no cofre (regra 7).
internal sealed class ConfiguracaoConfiguracaoEmail : IEntityTypeConfiguration<ConfiguracaoEmail>
{
    public void Configure(EntityTypeBuilder<ConfiguracaoEmail> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("configuracao_email");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Habilitado).IsRequired();
        builder.Property(x => x.Servidor).HasMaxLength(ConfiguracaoEmail.TamanhoMaximoServidor).IsRequired();
        builder.Property(x => x.Porta).IsRequired();
        builder.Property(x => x.Seguranca).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Usuario).HasMaxLength(ConfiguracaoEmail.TamanhoMaximoUsuario).IsRequired();
        builder.Property(x => x.RemetenteEmail).HasMaxLength(ConfiguracaoEmail.TamanhoMaximoEmail).IsRequired();
        builder.Property(x => x.RemetenteNome).HasMaxLength(ConfiguracaoEmail.TamanhoMaximoNome).IsRequired();
        builder.Property(x => x.UrlPublicaPortal).HasMaxLength(ConfiguracaoEmail.TamanhoMaximoUrl).IsRequired();
        builder.Property(x => x.Tema).HasMaxLength(ConfiguracaoEmail.TamanhoMaximoTema).IsRequired().HasDefaultValue(TemasEmail.Padrao);
        builder.Property(x => x.CorDestaque).HasMaxLength(7).IsRequired().HasDefaultValue(string.Empty);
        builder.Property(x => x.TextoRodape).HasMaxLength(ConfiguracaoEmail.TamanhoMaximoTextoRodape).IsRequired().HasDefaultValue(string.Empty);
        builder.Property(x => x.AtualizadoEmUtc).IsRequired();
        builder.Property(x => x.AtualizadoPorUsuarioId);
        builder.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();
    }
}
