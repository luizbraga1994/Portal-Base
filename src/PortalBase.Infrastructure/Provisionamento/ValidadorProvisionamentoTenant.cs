using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using PortalBase.Application.Provisionamento;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.BaseTenant;

namespace PortalBase.Infrastructure.Provisionamento;

/// <summary>
/// Validação PÓS-provisionamento / de REATIVAÇÃO (Prompt 09, seções 4, 15, 22). "Ready/Active"
/// nunca é presumido só porque <c>CREATE DATABASE</c> rodou: confere base existente, sem
/// migrations pendentes, conectividade com a credencial de RUNTIME e presença das tabelas
/// essenciais. Qualquer falha ⇒ inválido (o provisionamento vira Failed; a ativação é recusada).
/// Nomes de base são validados por regex restrito e a checagem de tabelas é PARAMETRIZADA.
/// </summary>
public sealed class ValidadorProvisionamentoTenant : IValidadorProvisionamento
{
    // Tabelas indispensáveis para operar (amostra representativa do schema do tenant). Acrescente aqui as tabelas
    // essenciais do domínio do seu portal: sem elas, o provisionamento falha (nunca presume "pronto").
    private static readonly string[] TabelasEssenciais =
        ["__EFMigrationsHistory", "arquivo_importacao", "marca_visual"];

    private readonly ConstrutorConexaoTenant _construtor;

    public ValidadorProvisionamentoTenant(ConstrutorConexaoTenant construtor)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        _construtor = construtor;
    }

    public async ValueTask<ResultadoValidacaoProvisionamento> ValidarAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);

        try
        {
            // 1) Sem migrations pendentes (schema completo) — via credencial admin.
            var conexaoAdmin = await _construtor.ConstruirAdminAsync(nomeBase, cancellationToken).ConfigureAwait(false);
            await using (var contexto = CriarContexto(conexaoAdmin))
            {
                var pendentes = await contexto.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
                if (pendentes.Any())
                {
                    return ResultadoValidacaoProvisionamento.Invalido("Migrations pendentes na base do tenant.");
                }
            }

            // 2) Tabelas essenciais presentes (checagem parametrizada em information_schema).
            var faltando = await TabelasFaltandoAsync(conexaoAdmin, nomeBase, cancellationToken).ConfigureAwait(false);
            if (faltando.Count > 0)
            {
                return ResultadoValidacaoProvisionamento.Invalido($"Tabelas essenciais ausentes: {string.Join(", ", faltando)}.");
            }

            // 3) Conectividade com a credencial de RUNTIME (a que a aplicação usa em operação).
            var conexaoRuntime = await _construtor.ConstruirRuntimeAsync(nomeBase, cancellationToken).ConfigureAwait(false);
            await using (var contexto = CriarContexto(conexaoRuntime))
            {
                if (!await contexto.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ResultadoValidacaoProvisionamento.Invalido("Runtime não conecta à base do tenant.");
                }
            }

            return ResultadoValidacaoProvisionamento.Valido;
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // Mensagem sanitizada (sem connection string/segredo): apenas o tipo do erro.
            return ResultadoValidacaoProvisionamento.Invalido($"Falha de validação da base ({excecao.GetType().Name}).");
        }
    }

    private static async ValueTask<IReadOnlyList<string>> TabelasFaltandoAsync(
        string conexaoAdmin,
        string nomeBase,
        CancellationToken cancellationToken)
    {
        await using var conexao = new MySqlConnection(conexaoAdmin);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);

        var presentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var comando = conexao.CreateCommand())
        {
            comando.CommandText =
                "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @base";
            comando.Parameters.AddWithValue("@base", nomeBase);

            await using var leitor = await comando.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await leitor.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                presentes.Add(leitor.GetString(0));
            }
        }

        return TabelasEssenciais.Where(t => !presentes.Contains(t)).ToArray();
    }

    private static PortalBaseTenantDbContext CriarContexto(string conexao)
    {
        var opcoes = new DbContextOptionsBuilder<PortalBaseTenantDbContext>()
            .UseMySQL(conexao)
            .Options;
        return new PortalBaseTenantDbContext(opcoes);
    }
}
