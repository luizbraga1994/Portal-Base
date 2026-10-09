using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Backup;
using PortalBase.Application.Observabilidade;
using PortalBase.Domain.Backup;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Contra o MySQL real (catálogo migrado): (1) a saúde dos workers gravada pelo host do Worker é lida pela Web — upsert
/// por nome (sem duplicar), código de erro limitado; (2) a guarda de idempotência do backup agendado considera só
/// backups NÃO falhos iniciados desde a janela; (3) a base do catálogo para o backup vem da conexão do servidor.
/// Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class SaudeWorkersEBackupAgendadoMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public SaudeWorkersEBackupAgendadoMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Saude_Gravada_Pelo_Worker_E_Lida_Em_Outro_Escopo_Sem_Duplicar()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var nome = "w-" + Guid.NewGuid().ToString("N")[..12];
        var t0 = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);

        using (var escopo = _fixture.CriarEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<IRepositorioSaudeWorkersPersistida>().GravarAsync(
                [new EstadoSaudeWorker(nome, true, t0, t0, null, 0, null, null, 0, 0)], t0);
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<IRepositorioSaudeWorkersPersistida>().GravarAsync(
                [new EstadoSaudeWorker(nome, false, t0, t0.AddMinutes(1), t0.AddMinutes(1), 5, t0.AddMinutes(1), new string('E', 300), 2, 1)], t0.AddMinutes(1));
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var lidos = await escopo.ServiceProvider.GetRequiredService<IRepositorioSaudeWorkersPersistida>().ListarAsync();
            var estado = lidos.Should().ContainSingle(e => e.Worker == nome).Subject;
            estado.Ativo.Should().BeFalse();
            estado.UltimaBatidaUtc.Should().Be(t0.AddMinutes(1));
            estado.TotalProcessado.Should().Be(5);
            estado.TotalErros.Should().Be(2);
            estado.Reinicios.Should().Be(1);
            estado.UltimoErroCodigo.Should().HaveLength(128, "o código de erro é limitado ao tamanho da coluna");
        }
    }

    [SkippableFact]
    public async Task Idempotencia_Do_Backup_Agendado_Ignora_Falhos_E_Anteriores_A_Janela()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        // Instantes únicos e distantes (a coleção compartilha o catálogo).
        var janela = new DateTimeOffset(2090, 1, 1, 3, 0, 0, TimeSpan.Zero).AddDays(Random.Shared.Next(0, 10_000));

        using var escopo = _fixture.CriarEscopo();
        var registros = escopo.ServiceProvider.GetRequiredService<IRepositorioRegistroBackup>();

        var anterior = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "catalogo_teste", janela.AddMinutes(-1));
        await registros.AdicionarAsync(anterior);
        var falho = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "catalogo_teste", janela.AddMinutes(1));
        await registros.AdicionarAsync(falho);
        falho.Falhar("IOException", janela.AddMinutes(2));
        await registros.AtualizarAsync(falho);

        var limite = janela.AddMinutes(4); // "em andamento" só conta se começou depois disto (JOB-001)
        (await registros.ExisteConcluidoOuEmAndamentoDesdeAsync(TipoBackup.CatalogoCompleto, janela, limite)).Should().BeFalse(
            "o anterior é de outra janela e o desta janela falhou ⇒ nova tentativa permitida");

        var emAndamento = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "catalogo_teste", janela.AddMinutes(5));
        await registros.AdicionarAsync(emAndamento);
        (await registros.ExisteConcluidoOuEmAndamentoDesdeAsync(TipoBackup.CatalogoCompleto, janela, limite)).Should().BeTrue(
            "backup em andamento recente já atende a janela (segunda instância não duplica)");
        (await registros.ExisteConcluidoOuEmAndamentoDesdeAsync(TipoBackup.CatalogoCompleto, janela, janela.AddMinutes(6))).Should().BeFalse(
            "um 'Iniciado' mais velho que o limite é de um processo que caiu: não atende a janela");
        (await registros.ExisteConcluidoOuEmAndamentoDesdeAsync(TipoBackup.TenantCompleto, janela, limite)).Should().BeFalse();

        // O interrompido aparece para a varredura (rastreado: a alteração grava sem conflito de concorrência).
        var interrompidos = await registros.ListarIniciadosAntesDeAsync(janela.AddMinutes(6));
        var lido = interrompidos.Should().ContainSingle(r => r.Id == emAndamento.Id).Subject;
        lido.Falhar("Interrompido", janela.AddMinutes(7));
        await registros.AtualizarAsync(lido);
        (await registros.ObterAsync(emAndamento.Id))!.Status.Should().Be(StatusBackup.Falhou);

        var concluido = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "catalogo_teste", janela.AddMinutes(8));
        await registros.AdicionarAsync(concluido);
        concluido.Concluir(new string('a', 64), 1, "teste", $"catalogo/teste-{concluido.Id:N}.bin", false, null, false, janela.AddMinutes(10), janela.AddMinutes(9));
        await registros.AtualizarAsync(concluido);
        (await registros.ExisteConcluidoOuEmAndamentoDesdeAsync(TipoBackup.CatalogoCompleto, janela, janela.AddDays(1))).Should().BeTrue(
            "concluído atende a janela independentemente do limite");
        (await registros.ListarConcluidosVencidosAsync(janela.AddMinutes(11))).Should().Contain(r => r.Id == concluido.Id);
        (await registros.ListarConcluidosVencidosAsync(janela.AddMinutes(9))).Should().NotContain(r => r.Id == concluido.Id);

        (await registros.ListarRecentesAsync(3)).Select(r => r.Id).Should().Contain(concluido.Id);
        escopo.ServiceProvider.GetRequiredService<IProvedorAlvosBackup>().NomeBaseCatalogo.Should().Be(_fixture.CatalogoDb);
    }
}
