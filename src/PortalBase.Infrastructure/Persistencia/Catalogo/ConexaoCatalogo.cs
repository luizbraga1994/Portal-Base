using MySql.Data.MySqlClient;

namespace PortalBase.Infrastructure.Persistencia.Catalogo;

/// <summary>
/// Connection string do banco de CATÁLOGO. Com <c>BancoCatalogo:SenhaRef</c>, a senha vem do COFRE (nome do segredo) e
/// <c>ConnectionStrings:Catalogo</c> fica sem senha — nenhuma senha na configuração do servidor (ex.: variáveis do pool do
/// IIS). Sem a referência, vale a connection string completa (variável de ambiente), como antes.
/// </summary>
public static class ConexaoCatalogo
{
    public const string ChaveSenhaRef = "BancoCatalogo:SenhaRef";

    /// <summary>Confere no arranque (sem ler segredo): com a referência, a connection string não pode trazer senha.</summary>
    public static void Validar(string conexao, string? senhaRef)
    {
        if (string.IsNullOrWhiteSpace(senhaRef))
        {
            return;
        }

        if (!string.IsNullOrEmpty(new MySqlConnectionStringBuilder(conexao).Password))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:Catalogo não pode ter senha quando {ChaveSenhaRef} está definida (a senha vem do cofre).");
        }
    }

    /// <summary>A connection string com a senha lida do cofre (o valor nunca é logado).</summary>
    public static string ComSenha(string conexao, string senha)
    {
        ArgumentException.ThrowIfNullOrEmpty(senha);
        return new MySqlConnectionStringBuilder(conexao) { Password = senha }.ConnectionString;
    }
}
