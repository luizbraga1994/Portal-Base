using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Tarefas;

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Configuracoes;

// TAREFAS EM SEGUNDO PLANO (ARQ-006, auditoria de 08/10/2026) — fila durável no CATÁLOGO. Só identificadores,
// parâmetros técnicos e resultado resumido (contagens/desfecho): nunca segredo, conteúdo de arquivo ou dado pessoal.

internal sealed class ConfiguracaoTarefaSegundoPlano : IEntityTypeConfiguration<TarefaSegundoPlano>
{
    public void Configure(EntityTypeBuilder<TarefaSegundoPlano> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("tarefa_segundo_plano");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Tipo).HasConversion<int>().IsRequired();
        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.SolicitanteId).IsRequired();
        builder.Property(x => x.Parametros).HasMaxLength(TarefaSegundoPlano.TamanhoMaximoParametros).IsRequired();
        builder.Property(x => x.ChaveIdempotencia).HasMaxLength(TarefaSegundoPlano.TamanhoMaximoChave).IsRequired();
        builder.Property(x => x.Estado).HasConversion<int>().IsRequired();
        builder.Property(x => x.Tentativas).IsRequired();
        builder.Property(x => x.CriadaEmUtc).IsRequired();
        builder.Property(x => x.IniciadaEmUtc);
        builder.Property(x => x.ConcluidaEmUtc);
        builder.Property(x => x.LeaseAteUtc);
        builder.Property(x => x.Executor).HasMaxLength(100);
        builder.Property(x => x.Resultado).HasMaxLength(TarefaSegundoPlano.TamanhoMaximoResultado);
        builder.Property(x => x.Motivo).HasMaxLength(TarefaSegundoPlano.TamanhoMaximoMotivo);
        builder.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();
        builder.Ignore(x => x.Ativa);

        // Busca do executor (estado + ordem de chegada) e do "mesmo pedido ainda ativo" (contribuinte + tipo + chave).
        builder.HasIndex(x => new { x.Estado, x.CriadaEmUtc }).HasDatabaseName("ix_tarefa_estado_criada");
        builder.HasIndex(x => new { x.TenantId, x.Tipo, x.ChaveIdempotencia }).HasDatabaseName("ix_tarefa_tenant_tipo_chave");
    }
}
