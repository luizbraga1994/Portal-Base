using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Prompt 81-R1 (F81-14) — atomicidade do <c>SaveChanges</c> no CATÁLOGO. O <see cref="IUnitOfWork"/> traduz
/// duplicidade (MySQL 1062) em <see cref="ViolacaoUnicidadeException"/> para que os casos de uso tratem o conflito
/// como "nada aconteceu"; isso só é verdade se o lote inteiro for desfeito. Também prova a compatibilidade com a
/// única transação explícita existente (<see cref="IUnitOfWork.ExecutarEmTransacaoAsync"/>): sem aninhamento.
/// Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class AtomicidadeCatalogoP81R1MySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public AtomicidadeCatalogoP81R1MySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    private static Usuario NovoUsuario(string login)
        => Usuario.Criar(login, "Usuário P81-R1", $"{login}@exemplo.com", DateTimeOffset.UtcNow);

    [SkippableFact]
    public async Task Lote_Com_Conflito_De_Unicidade_Nao_Grava_Nenhuma_Linha()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var alterado = NovoUsuario($"p81r1_al_{sufixo}");
        var existente = NovoUsuario($"p81r1_ex_{sufixo}");
        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            catalogo.Usuarios.AddRange(alterado, existente);
            await catalogo.SaveChangesAsync();
        }

        // Um único SaveChanges com o mesmo formato do F81-14 (UPDATE de um agregado + INSERTs): o EF ordena o
        // UPDATE antes dos INSERTs e a última inserção colide no índice único de login. Sem transação, o UPDATE
        // (e a 1ª inserção) ficariam confirmados em autocommit apesar da exceção.
        var valido = NovoUsuario($"p81r1_ok_{sufixo}");
        var duplicado = Usuario.Criar(existente.Login, "Duplicado", $"dup_{sufixo}@exemplo.com", DateTimeOffset.UtcNow);
        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var unidade = escopo.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var rastreado = await catalogo.Usuarios.SingleAsync(u => u.Id == alterado.Id);
            rastreado.Inativar(DateTimeOffset.UtcNow);
            catalogo.Usuarios.Add(valido);
            catalogo.Usuarios.Add(duplicado);

            var salvar = async () => await unidade.SalvarAlteracoesAsync();
            await salvar.Should().ThrowAsync<ViolacaoUnicidadeException>();
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var relido = await catalogo.Usuarios.AsNoTracking().SingleAsync(u => u.Id == alterado.Id);
            using (new AssertionScope())
            {
                relido.Status.Should().Be(StatusUsuario.Ativo, "o conflito desfaz o lote INTEIRO — o UPDATE não fica confirmado");
                relido.ConcurrencyStamp.Should().Be(alterado.ConcurrencyStamp);
                (await catalogo.Usuarios.AsNoTracking().AnyAsync(u => u.Id == valido.Id))
                    .Should().BeFalse("nenhuma inserção parcial do SaveChanges que falhou");
                (await catalogo.Usuarios.AsNoTracking().CountAsync(u => u.Login == existente.Login)).Should().Be(1);
            }
        }
    }

    [SkippableFact]
    public async Task Transacao_Explicita_E_Reutilizada_Pelo_SaveChanges_Sem_Aninhamento()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        // Com AutoTransactionBehavior.Always, o SaveChanges dentro de ExecutarEmTransacaoAsync deve REUTILIZAR a
        // transação explícita: sem transação aninhada (MySQL não suporta) e sem commit próprio — o rollback da
        // transação externa desfaz os DOIS SaveChanges; o commit confirma os dois.
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var desfeito1 = NovoUsuario($"p81r1_r1_{sufixo}");
        var desfeito2 = NovoUsuario($"p81r1_r2_{sufixo}");
        var confirmado1 = NovoUsuario($"p81r1_c1_{sufixo}");
        var confirmado2 = NovoUsuario($"p81r1_c2_{sufixo}");

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var unidade = escopo.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var falhar = async () => await unidade.ExecutarEmTransacaoAsync(async ct =>
            {
                catalogo.Usuarios.Add(desfeito1);
                await unidade.SalvarAlteracoesAsync(ct);
                catalogo.Usuarios.Add(desfeito2);
                await unidade.SalvarAlteracoesAsync(ct);
                throw new InvalidOperationException("falha após dois SaveChanges");
            });
            await falhar.Should().ThrowAsync<InvalidOperationException>();
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var unidade = escopo.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unidade.ExecutarEmTransacaoAsync(async ct =>
            {
                catalogo.Usuarios.Add(confirmado1);
                await unidade.SalvarAlteracoesAsync(ct);
                catalogo.Usuarios.Add(confirmado2);
                await unidade.SalvarAlteracoesAsync(ct);
            });
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            async Task<bool> GravadoAsync(Guid id) => await catalogo.Usuarios.AsNoTracking().AnyAsync(u => u.Id == id);
            using (new AssertionScope())
            {
                (await GravadoAsync(desfeito1.Id)).Should().BeFalse("o rollback externo desfaz o 1º SaveChanges interno");
                (await GravadoAsync(desfeito2.Id)).Should().BeFalse("o rollback externo desfaz o 2º SaveChanges interno");
                (await GravadoAsync(confirmado1.Id)).Should().BeTrue("o commit externo confirma o 1º SaveChanges interno");
                (await GravadoAsync(confirmado2.Id)).Should().BeTrue("o commit externo confirma o 2º SaveChanges interno");
            }
        }
    }
}
