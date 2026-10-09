namespace PortalBase.Application.Abstracoes;

/// <summary>Relógio abstrato (regra 1: tempo testável e determinístico).</summary>
public interface IRelogio
{
    DateTimeOffset AgoraUtc { get; }
}

/// <summary>
/// Provedor de segredos (regra 7). Resolve uma referência (ex.: nome no cofre)
/// para a credencial real em runtime. Segredos nunca são versionados nem logados.
/// </summary>
public interface IProvedorSegredos
{
    ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default);
}

/// <summary>
/// ESCRITA de segredo no cofre (regra 7) — quando uma credencial precisa ser DEFINIDA/ROTACIONADA em runtime
/// (ex.: a senha do SMTP mudou). O valor vai SOMENTE para o cofre gerenciado, NUNCA para banco, arquivo ou log.
/// Nem todo provedor suporta escrita (fail-closed com erro claro quando não suportado). O chamador informa a
/// referência já resolvida no servidor; a autorização/auditoria é feita na camada de acesso por tenant.
/// </summary>
public interface IEscritorSegredos
{
    ValueTask DefinirAsync(string referencia, string valor, CancellationToken cancellationToken = default);
}

/// <summary>
/// Unidade de trabalho / transação (abstração de persistência para a Application,
/// sem acoplar a EF Core). Toda operação repetível deve poder ser idempotente (regra 12).
/// </summary>
public interface IUnitOfWork
{
    Task<int> SalvarAlteracoesAsync(CancellationToken cancellationToken = default);

    Task ExecutarEmTransacaoAsync(Func<CancellationToken, Task> operacao, CancellationToken cancellationToken = default);
}

/// <summary>
/// Armazenamento de arquivos fora da aplicação (regra: arquivos enviados NÃO ficam
/// dentro do processo/host). Validação de conteúdo/tamanho é responsabilidade do chamador.
/// </summary>
public interface IArmazenamentoArquivos
{
    ValueTask<string> SalvarAsync(string caminhoRelativo, Stream conteudo, CancellationToken cancellationToken = default);

    ValueTask<Stream> AbrirLeituraAsync(string referencia, CancellationToken cancellationToken = default);

    ValueTask RemoverAsync(string referencia, CancellationToken cancellationToken = default);
}
