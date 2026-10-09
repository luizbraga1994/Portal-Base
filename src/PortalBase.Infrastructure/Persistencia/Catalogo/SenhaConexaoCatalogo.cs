using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PortalBase.Application.Abstracoes;

namespace PortalBase.Infrastructure.Persistencia.Catalogo;

/// <summary>
/// Coloca a senha do catálogo (lida do COFRE, <c>BancoCatalogo:SenhaRef</c>) na conexão só no momento em que ela abre,
/// de forma ASSÍNCRONA — INT-002 (auditoria de 08/10/2026). Antes, a senha era lida de forma síncrona
/// (sync-over-async) toda vez que um DbContext do catálogo era criado, mesmo sem consulta: com o cofre lento (Key
/// Vault), cada tela aberta prendia uma thread esperando o cofre. O valor vem do cache do provedor (uma busca por vez,
/// valor anterior servido se o cofre piscar) e nunca é logado; a connection string configurada continua sem senha.
/// </summary>
public sealed class SenhaConexaoCatalogo : DbConnectionInterceptor
{
    private readonly IProvedorSegredos _segredos;
    private readonly string _conexaoSemSenha;
    private readonly string _senhaRef;

    public SenhaConexaoCatalogo(IProvedorSegredos segredos, string conexaoSemSenha, string senhaRef)
    {
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentException.ThrowIfNullOrWhiteSpace(conexaoSemSenha);
        ArgumentException.ThrowIfNullOrWhiteSpace(senhaRef);
        _segredos = segredos;
        _conexaoSemSenha = conexaoSemSenha;
        _senhaRef = senhaRef;
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var senha = await _segredos.ObterAsync(_senhaRef, cancellationToken).ConfigureAwait(false);
        connection.ConnectionString = ConexaoCatalogo.ComSenha(_conexaoSemSenha, senha);
        return result;
    }

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Abertura SÍNCRONA (rara: o portal usa a API assíncrona; sobra para ferramentas). O cofre só tem API
        // assíncrona; aqui a espera é inevitável, mas na prática servida pelo cache, sem ir ao cofre.
        var senha = _segredos.ObterAsync(_senhaRef).AsTask().GetAwaiter().GetResult();
        connection.ConnectionString = ConexaoCatalogo.ComSenha(_conexaoSemSenha, senha);
        return result;
    }
}
