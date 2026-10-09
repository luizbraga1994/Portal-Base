using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using PortalBase.Application.Abstracoes;

namespace PortalBase.Infrastructure.Segredos;

/// <summary>
/// Provedor de segredos para <b>Development/Test apenas</b> (Prompt 06, seção 6): resolve
/// <c>Segredos:&lt;ref&gt;</c> de variáveis de ambiente/user-secrets, atrás da porta
/// <see cref="IProvedorSegredos"/>. NUNCA é registrado em Produção — o
/// <see cref="ConfiguracaoSegredos"/> exige o cofre gerenciado lá (fail-fast). Não usar valores
/// reais aqui; segredos NUNCA são versionados (regra 7).
///
/// Suporta ESCRITA (<see cref="IEscritorSegredos"/>) via um overlay EM MEMÓRIA (processo atual, nunca logado) — e,
/// em Development, também no arquivo de user-secrets da aplicação (<c>Vault:ArquivoSegredosLocal</c>, no perfil do
/// usuário, fora do repositório), para que os segredos definidos pela tela sobrevivam ao
/// reinício. Em Produção o cofre gerenciado é obrigatório e este provedor nunca é registrado.
/// </summary>
public sealed class ProvedorSegredosConfiguracao : IProvedorSegredos, IEscritorSegredos
{
    private const string Prefixo = "Segredos:";

    // Uma gravação do arquivo local por vez (no processo).
    private static readonly SemaphoreSlim TravaArquivo = new(1, 1);

    private readonly IConfiguration _configuracao;
    private readonly string? _arquivoLocal;
    private readonly ConcurrentDictionary<string, string> _overlay = new(StringComparer.Ordinal);

    public ProvedorSegredosConfiguracao(IConfiguration configuracao)
        : this(configuracao, null)
    {
    }

    /// <param name="configuracao">Configuração de onde vêm os <c>Segredos:&lt;ref&gt;</c>.</param>
    /// <param name="arquivoLocal">
    /// Development: arquivo JSON de user-secrets onde as escritas também são guardadas; nulo = só memória.
    /// </param>
    public ProvedorSegredosConfiguracao(IConfiguration configuracao, string? arquivoLocal)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        _configuracao = configuracao;
        _arquivoLocal = string.IsNullOrWhiteSpace(arquivoLocal) ? null : arquivoLocal;
    }

    public ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencia);

        if (_overlay.TryGetValue(referencia, out var definido))
        {
            return ValueTask.FromResult(definido);
        }

        var valor = _configuracao[$"{Prefixo}{referencia}"];
        if (string.IsNullOrEmpty(valor))
        {
            // Fail-closed: sem o segredo, não há conexão (a mensagem não expõe o valor).
            throw new InvalidOperationException($"Segredo '{referencia}' não configurado.");
        }

        return ValueTask.FromResult(valor);
    }

    public async ValueTask DefinirAsync(string referencia, string valor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencia);
        ArgumentException.ThrowIfNullOrEmpty(valor);

        // Primeiro o arquivo (se houver): se ele falhar, nada muda e o chamador recebe o erro (fail-closed).
        if (_arquivoLocal is not null)
        {
            await GravarNoArquivoAsync(_arquivoLocal, $"{Prefixo}{referencia}", valor, cancellationToken).ConfigureAwait(false);
        }

        _overlay[referencia] = valor; // vale já neste processo — nunca logado
    }

    // Grava a chave plana no JSON de user-secrets (mesmo formato do 'dotnet user-secrets set'), preservando as outras
    // chaves; escreve num arquivo temporário e troca (nunca deixa o arquivo pela metade). JSON inválido não é
    // sobrescrito.
    private static async Task GravarNoArquivoAsync(string caminho, string chave, string valor, CancellationToken cancellationToken)
    {
        await TravaArquivo.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject raiz;
            if (File.Exists(caminho))
            {
                var texto = await File.ReadAllTextAsync(caminho, cancellationToken).ConfigureAwait(false);
                try
                {
                    raiz = string.IsNullOrWhiteSpace(texto)
                        ? []
                        : JsonNode.Parse(texto, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                            ?? throw new InvalidOperationException("O arquivo local de segredos não é um objeto JSON.");
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("O arquivo local de segredos (user-secrets) está inválido; corrija-o antes de definir segredos pela tela.");
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(caminho))!);
                raiz = [];
            }

            raiz[chave] = valor;
            var temporario = caminho + ".tmp";
            await File.WriteAllTextAsync(temporario, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            File.Move(temporario, caminho, overwrite: true);
        }
        finally
        {
            TravaArquivo.Release();
        }
    }
}
