using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using PortalBase.Infrastructure.DependencyInjection;

namespace PortalBase.Infrastructure.Persistencia.Catalogo;

/// <summary>O que <see cref="GarantiaCatalogoDesenvolvimento"/> encontrou/fez.</summary>
public enum SituacaoGarantiaCatalogo
{
    /// <summary>Base e tabelas já existiam e estavam em dia.</summary>
    EmDia = 1,

    /// <summary>A base ou tabelas faltavam (ou havia migrations pendentes) e foram criadas/aplicadas.</summary>
    Reparado = 2,

    /// <summary>Sem <c>ConnectionStrings:CatalogoMigracao</c>: nada pode ser criado (só verificação).</summary>
    SemCredencialDeMigracao = 3,

    /// <summary>MySQL inacessível, credencial recusada ou configuração inconsistente.</summary>
    Indisponivel = 4,
}

public sealed record ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo Situacao, bool BaseCriada, int MigracoesAplicadas, string Mensagem);

/// <summary>
/// SÓ DESENVOLVIMENTO: verifica se a base de catálogo e as suas tabelas existem e, se faltarem (ex.: base apagada por
/// engano), cria a base com o mesmo charset/collation do script de ambiente e aplica as migrations. Usa a conta
/// MIGRADORA (<c>ConnectionStrings:CatalogoMigracao</c>), restrita à base de catálogo e gravada pelo
/// <c>configurar-ambiente-local.ps1</c> só para a Web — a conta de runtime continua só com DML (menor privilégio).
/// Em Produção nada disto roda (o host só chama em Development) e o preflight recusa a credencial: lá um catálogo
/// apagado é incidente (restaurar backup), nunca algo a recriar vazio em silêncio.
/// </summary>
public sealed partial class GarantiaCatalogoDesenvolvimento
{
    /// <summary>Nome da connection string da conta migradora.</summary>
    public const string NomeConexaoMigracao = "CatalogoMigracao";

    private const string ComandoScript = ".\\scripts\\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo";

    private readonly IConfiguration _configuracao;
    private readonly ILogger<GarantiaCatalogoDesenvolvimento> _log;

    public GarantiaCatalogoDesenvolvimento(IConfiguration configuracao, ILogger<GarantiaCatalogoDesenvolvimento> log)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        ArgumentNullException.ThrowIfNull(log);
        _configuracao = configuracao;
        _log = log;
    }

    public async Task<ResultadoGarantiaCatalogo> GarantirAsync(CancellationToken cancellationToken = default)
    {
        var runtime = _configuracao.GetConnectionString(ServicosInfrastructure.NomeConexaoCatalogo);
        var migracao = _configuracao.GetConnectionString(NomeConexaoMigracao);
        if (string.IsNullOrWhiteSpace(runtime))
        {
            return Registrar(new ResultadoGarantiaCatalogo(
                SituacaoGarantiaCatalogo.Indisponivel, false, 0, $"ConnectionStrings:Catalogo ausente. Rode: {ComandoScript}"));
        }

        if (string.IsNullOrWhiteSpace(migracao))
        {
            return Registrar(await VerificarSemCredencialAsync(runtime, cancellationToken).ConfigureAwait(false));
        }

        string nomeBase;
        string servidorMigracao;
        try
        {
            var construtorRuntime = new MySqlConnectionStringBuilder(runtime);
            var construtorMigracao = new MySqlConnectionStringBuilder(migracao);
            nomeBase = construtorRuntime.Database ?? string.Empty;
            if (!NomeBaseSeguro().IsMatch(nomeBase)
                || !string.Equals(construtorMigracao.Database, nomeBase, StringComparison.Ordinal)
                || !string.Equals(construtorMigracao.Server, construtorRuntime.Server, StringComparison.OrdinalIgnoreCase)
                || construtorMigracao.Port != construtorRuntime.Port)
            {
                return Registrar(new ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo.Indisponivel, false, 0,
                    $"ConnectionStrings:CatalogoMigracao precisa apontar para o MESMO servidor e a MESMA base do catálogo. Rode: {ComandoScript}"));
            }

            construtorMigracao.Database = string.Empty; // a base pode não existir: conecta no servidor
            servidorMigracao = construtorMigracao.ConnectionString;
        }
        catch (ArgumentException)
        {
            // A mensagem do parser pode citar trechos da connection string (senha): não é repassada (regra 16).
            return Registrar(new ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo.Indisponivel, false, 0,
                $"Connection string do catálogo inválida. Rode: {ComandoScript}"));
        }

        try
        {
            var baseCriada = await CriarBaseSeAusenteAsync(servidorMigracao, nomeBase, cancellationToken).ConfigureAwait(false);

            var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>().UseMySQL(migracao).Options;
            await using var contexto = new PortalBaseCatalogoDbContext(opcoes);
            var pendentes = (await contexto.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count();
            if (pendentes > 0)
            {
                await contexto.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            return Registrar(baseCriada || pendentes > 0
                ? new ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo.Reparado, baseCriada, pendentes,
                    baseCriada
                        ? $"Catálogo '{nomeBase}' não existia: base criada e {pendentes} migration(s) aplicada(s) (Development)."
                        : $"Catálogo '{nomeBase}': {pendentes} migration(s) pendente(s) aplicada(s) (Development).")
                : new ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo.EmDia, false, 0, $"Catálogo '{nomeBase}' em dia."));
        }
        catch (MySqlException excecao)
        {
            // Mensagens do MySQL não trazem a senha; ainda assim só o essencial vai para o log (regra 8).
            return Registrar(new ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo.Indisponivel, false, 0,
                $"Não foi possível garantir o catálogo '{nomeBase}' (MySQL {excecao.Number.ToString(CultureInfo.InvariantCulture)}: {excecao.Message}). " +
                $"O MySQL está no ar? Se as contas mudaram, rode: {ComandoScript}"));
        }
    }

    private static async Task<bool> CriarBaseSeAusenteAsync(string servidor, string nomeBase, CancellationToken cancellationToken)
    {
        await using var conexao = new MySqlConnection(servidor);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var consulta = conexao.CreateCommand())
        {
            consulta.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @nome";
            consulta.Parameters.AddWithValue("@nome", nomeBase);
            var existentes = Convert.ToInt64(await consulta.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (existentes > 0)
            {
                return false;
            }
        }

        await using var criar = conexao.CreateCommand();
#pragma warning disable CA2100 // Identificador validado por regex restrito (NomeBaseSeguro); DDL não aceita parâmetro.
        criar.CommandText = $"CREATE DATABASE IF NOT EXISTS `{nomeBase}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs";
#pragma warning restore CA2100
        await criar.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Sem a conta migradora: só diz o que falta e como resolver (nada é criado).</summary>
    private static async Task<ResultadoGarantiaCatalogo> VerificarSemCredencialAsync(string runtime, CancellationToken cancellationToken)
    {
        try
        {
            var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>().UseMySQL(runtime).Options;
            await using var contexto = new PortalBaseCatalogoDbContext(opcoes);
            if (await contexto.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                && !(await contexto.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
            {
                return new ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo.EmDia, false, 0, "Catálogo em dia.");
            }
        }
        catch (MySqlException)
        {
            // cai na orientação abaixo
        }

        return new ResultadoGarantiaCatalogo(SituacaoGarantiaCatalogo.SemCredencialDeMigracao, false, 0,
            "Catálogo ausente, sem tabelas ou com migrations pendentes, e sem a conta migradora para recriá-lo " +
            $"(ConnectionStrings:CatalogoMigracao). Rode: {ComandoScript}");
    }

    private ResultadoGarantiaCatalogo Registrar(ResultadoGarantiaCatalogo resultado)
    {
        switch (resultado.Situacao)
        {
            case SituacaoGarantiaCatalogo.EmDia:
                LogEmDia(_log, resultado.Mensagem);
                break;
            case SituacaoGarantiaCatalogo.Reparado:
                LogReparado(_log, resultado.Mensagem);
                break;
            default:
                LogProblema(_log, resultado.Mensagem);
                break;
        }

        return resultado;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex NomeBaseSeguro();

    [LoggerMessage(EventId = 3101, Level = LogLevel.Debug, Message = "Garantia do catálogo (Development): {Mensagem}")]
    private static partial void LogEmDia(ILogger logger, string mensagem);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Warning, Message = "Garantia do catálogo (Development): {Mensagem}")]
    private static partial void LogReparado(ILogger logger, string mensagem);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Error, Message = "Garantia do catálogo (Development): {Mensagem}")]
    private static partial void LogProblema(ILogger logger, string mensagem);
}
