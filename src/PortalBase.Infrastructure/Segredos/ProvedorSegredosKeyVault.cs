using Azure;
using Azure.Security.KeyVault.Secrets;
using PortalBase.Application.Abstracoes;

namespace PortalBase.Infrastructure.Segredos;

/// <summary>
/// Provedor de segredos sobre um COFRE GERENCIADO (Azure Key Vault). A autenticação é por
/// identidade da aplicação (<c>DefaultAzureCredential</c> → Managed/Workload Identity), sem
/// client secret estático (seções 5, 22). Fail-closed: segredo ausente ou cofre indisponível
/// resulta em erro — nunca em fallback silencioso (seção 7). A mensagem nunca expõe o valor.
/// </summary>
public sealed class ProvedorSegredosKeyVault : IProvedorSegredos, IEscritorSegredos
{
    private readonly SecretClient _cliente;

    public ProvedorSegredosKeyVault(SecretClient cliente)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        _cliente = cliente;
    }

    public async ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencia);

        try
        {
            var resposta = await _cliente.GetSecretAsync(referencia, cancellationToken: cancellationToken).ConfigureAwait(false);
            return resposta.Value.Value;
        }
        catch (RequestFailedException excecao) when (excecao.Status == 404)
        {
            // Nome inexistente: erro claro, sem expor conteúdo.
            throw new InvalidOperationException($"Segredo '{referencia}' não encontrado no cofre.");
        }
    }

    /// <summary>Define/rotaciona o segredo no cofre gerenciado (nova versão). O valor NUNCA é logado.</summary>
    public async ValueTask DefinirAsync(string referencia, string valor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencia);
        ArgumentException.ThrowIfNullOrEmpty(valor);
        await _cliente.SetSecretAsync(referencia, valor, cancellationToken).ConfigureAwait(false);
    }
}
