using System.Text.RegularExpressions;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Deriva o nome físico da base individual do contribuinte a partir do <see cref="NomeTenant"/> ESCOLHIDO na criação
/// (ex.: STAKE → <c>portalbase_tenant_stake</c>). O nome nunca vem de requisição em tempo de uso: é gravado no catálogo
/// na criação (operação administrativa) e lido de lá; mesmo assim é revalidado contra um padrão restrito antes de
/// qualquer uso (regra 13 / seção 8): defesa contra injeção em nomes de banco. Tenants antigos mantêm o nome
/// <c>tenant_&lt;guid&gt;</c> já gravado.
/// </summary>
public static partial class NomeBaseFisicaTenant
{
    public static string Derivar(NomeTenant nome)
    {
        ArgumentNullException.ThrowIfNull(nome);
        var nomeBase = nome.NomeBaseFisica;

        // Garantia adicional: só aceita o padrão seguro esperado.
        if (!PadraoSeguro().IsMatch(nomeBase))
        {
            throw new EntradaInvalidaException("Nome físico de base gerado é inválido.");
        }

        return nomeBase;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex PadraoSeguro();
}
