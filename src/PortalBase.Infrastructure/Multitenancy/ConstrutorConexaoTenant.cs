using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using PortalBase.Application.Abstracoes;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Multitenancy;

/// <summary>
/// Monta a connection string de uma base de tenant SOMENTE a partir de fontes confiáveis:
/// servidor/pool da configuração, nome da base (validado, derivado do catálogo — nunca do
/// cliente, seções 10/11) e senha do cofre. O caller nunca fornece host/porta/usuário/senha.
/// </summary>
public sealed partial class ConstrutorConexaoTenant
{
    // Esquemas de sistema do MySQL: passam no padrão sintático, mas NUNCA podem ser
    // usados como base de tenant (evita colisão/escalonamento contra o servidor).
    private static readonly HashSet<string> EsquemasReservados =
        new(StringComparer.Ordinal) { "mysql", "information_schema", "performance_schema", "sys" };

    private readonly OpcoesBancoTenant _opcoes;
    private readonly IProvedorSegredos _segredos;

    public ConstrutorConexaoTenant(IOptions<OpcoesBancoTenant> opcoes, IProvedorSegredos segredos)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(segredos);
        _opcoes = opcoes.Value;
        _segredos = segredos;
    }

    /// <summary>Conexão com a credencial de RUNTIME (DML) para a base do tenant.</summary>
    public ValueTask<string> ConstruirRuntimeAsync(string nomeBase, CancellationToken cancellationToken = default)
        => ConstruirAsync(nomeBase, _opcoes.RuntimeUsuario, _opcoes.RuntimeSenhaRef, pooling: true, cancellationToken);

    /// <summary>
    /// Conexão com a credencial ADMINISTRATIVA (DDL/grants). <paramref name="nomeBase"/> nulo = nível servidor.
    /// P62-R1: a conexão admin ligada a UMA base de tenant (migração/validação de provisionamento) é SEM pool —
    /// operação pontual; com pool, cada base provisionada retinha uma sessão administrativa ociosa no processo
    /// (uma por connection string), esgotando max_connections. A de nível servidor segue com pool (uma só).
    /// </summary>
    public ValueTask<string> ConstruirAdminAsync(string? nomeBase, CancellationToken cancellationToken = default)
        => ConstruirAsync(nomeBase, _opcoes.AdminUsuario, _opcoes.AdminSenhaRef, pooling: nomeBase is null, cancellationToken);

    /// <summary>Valida rigorosamente um nome físico de base antes de qualquer uso em DDL.</summary>
    public static string ValidarNomeBase(string nomeBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeBase);
        if (!PadraoNomeBase().IsMatch(nomeBase))
        {
            throw new EntradaInvalidaException("Nome de base do tenant inválido.");
        }

        if (EsquemasReservados.Contains(nomeBase))
        {
            throw new EntradaInvalidaException("Nome de base do tenant reservado pelo servidor.");
        }

        return nomeBase;
    }

    /// <summary>Valida um nome de usuário MySQL antes de usá-lo em GRANT.</summary>
    public static string ValidarUsuario(string usuario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usuario);
        if (!PadraoUsuario().IsMatch(usuario))
        {
            throw new EntradaInvalidaException("Nome de usuário MySQL inválido.");
        }

        return usuario;
    }

    private async ValueTask<string> ConstruirAsync(
        string? nomeBase, string usuario, string senhaRef, bool pooling, CancellationToken cancellationToken)
    {
        ValidarUsuario(usuario);

        var senha = await _segredos.ObterAsync(senhaRef, cancellationToken).ConfigureAwait(false);

        var construtor = new MySqlConnectionStringBuilder
        {
            Server = _opcoes.Servidor,
            Port = _opcoes.Porta,
            UserID = usuario,
            Password = senha,
            Pooling = pooling,
            ConnectionTimeout = _opcoes.TimeoutConexaoSegundos,
            SslMode = _opcoes.SslMode, // §18: TLS exigido por padrão (nunca "Preferred" — sem downgrade silencioso)
            // Padrão false (produção usa TLS). Só true no MySQL de teste/CI sem TLS (caching_sha2 não-TLS).
            AllowPublicKeyRetrieval = _opcoes.PermitirRecuperacaoChavePublica,
        };

        if (pooling)
        {
            construtor.MaximumPoolSize = _opcoes.MaximoPool;
            construtor.MinimumPoolSize = _opcoes.MinimoPool;
            construtor.ConnectionLifeTime = _opcoes.VidaConexaoSegundos;
        }

        if (nomeBase is not null)
        {
            construtor.Database = ValidarNomeBase(nomeBase);
        }

        return construtor.ConnectionString;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex PadraoNomeBase();

    [GeneratedRegex("^[a-zA-Z0-9_]{1,32}$")]
    private static partial Regex PadraoUsuario();
}
