using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Seguranca;
using PortalBase.Domain.Arquivos;

namespace PortalBase.Application.Ingestao;

/// <summary>Comando de ingestão. O conteúdo é um stream sequencial (não confiável até validado).</summary>
public sealed record ComandoIngestao(string? NomeOriginal, Stream Conteudo);

/// <summary>Desfecho da ingestão de um arquivo.</summary>
public enum SituacaoIngestao
{
    Aceito = 1,
    Duplicado = 2,
    Rejeitado = 3,
}

/// <summary>
/// Resultado da ingestão. Em rejeição, <see cref="Motivo"/> é um código GENÉRICO e sanitizado
/// (nunca caminho, stack, PII ou detalhe interno — regra 16). Em duplicidade, a operação é
/// idempotente: o mesmo conteúdo já existe no banco do tenant.
/// </summary>
public sealed record ResultadoIngestao(SituacaoIngestao Situacao, Guid? ArquivoId, string? Motivo)
{
    public static ResultadoIngestao Aceito(Guid id) => new(SituacaoIngestao.Aceito, id, null);

    public static ResultadoIngestao Duplicado(Guid? id) => new(SituacaoIngestao.Duplicado, id, null);

    public static ResultadoIngestao Rejeitado(string motivo) => new(SituacaoIngestao.Rejeitado, null, motivo);
}

/// <summary>
/// Caso de uso de INGESTÃO SEGURA de arquivo (Prompt 08). Orquestra, na ORDEM, os controles já
/// existentes: (1) TenantContext autorizado server-side (fail-closed — regra 5); (2) autorização
/// da permissão <c>dados.importar</c> NO tenant (regras 3, 15); (3) derivação do nome/extensão no
/// servidor (nunca confiar no cliente — regra 2); (4) allowlist de extensão; (5) streaming para
/// área temporária privada com hash SHA-256 e limite de tamanho aplicado incrementalmente;
/// (6) validação do CONTEÚDO real (magic bytes / anti ZIP bomb); (7) idempotência por hash no
/// tenant; (8) antivírus fail-closed (rejeita se obrigatório e indisponível/erro; nunca
/// "considera limpo"); (9) armazenamento em área privada tenant-scoped (nome gerado, fora de
/// wwwroot); (10) metadados no banco do TENANT; (11) auditoria de cada desfecho. O conteúdo NUNCA
/// é logado; motivos de rejeição são genéricos.
/// </summary>
public sealed class IngerirArquivo
{
    private const int TamanhoBuffer = 81920;

    private readonly ITenantContext _tenant;
    private readonly IContextoSeguranca _seguranca;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IAreaTrabalhoTemporaria _areaTemporaria;
    private readonly IValidadorConteudoArquivo _validadorConteudo;
    private readonly IScannerAntivirus _antivirus;
    private readonly IRepositorioArquivoImportacao _repositorio;
    private readonly IArmazenamentoArquivos _armazenamento;
    private readonly IAuditoria _auditoria;
    private readonly IContextoAuditoria _correlacao;
    private readonly IPoliticaRetencao _retencao;
    private readonly IRelogio _relogio;
    private readonly OpcoesUpload _opcoes;

    public IngerirArquivo(
        ITenantContext tenant,
        IContextoSeguranca seguranca,
        IServicoAutorizacao autorizacao,
        IAreaTrabalhoTemporaria areaTemporaria,
        IValidadorConteudoArquivo validadorConteudo,
        IScannerAntivirus antivirus,
        IRepositorioArquivoImportacao repositorio,
        IArmazenamentoArquivos armazenamento,
        IAuditoria auditoria,
        IContextoAuditoria correlacao,
        IPoliticaRetencao retencao,
        IRelogio relogio,
        OpcoesUpload opcoes)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(areaTemporaria);
        ArgumentNullException.ThrowIfNull(validadorConteudo);
        ArgumentNullException.ThrowIfNull(antivirus);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(armazenamento);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(correlacao);
        ArgumentNullException.ThrowIfNull(retencao);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);

        _tenant = tenant;
        _seguranca = seguranca;
        _autorizacao = autorizacao;
        _areaTemporaria = areaTemporaria;
        _validadorConteudo = validadorConteudo;
        _antivirus = antivirus;
        _repositorio = repositorio;
        _armazenamento = armazenamento;
        _auditoria = auditoria;
        _correlacao = correlacao;
        _retencao = retencao;
        _relogio = relogio;
        _opcoes = opcoes;
    }

    public async Task<ResultadoIngestao> ExecutarAsync(ComandoIngestao comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comando);
        ArgumentNullException.ThrowIfNull(comando.Conteudo);

        // (1) Fail-closed: nenhuma operação de dado do tenant sem TenantContext válido e autorizado.
        if (!_tenant.EstaAutorizado || _tenant.TenantId is not Guid tenantId)
        {
            throw new TenantContextAusenteException();
        }

        // (2) Autorização server-side no tenant (deny-by-default; audita a negação e lança).
        await _autorizacao.ExigirAsync(tenantId, Permissoes.DadosImportar, cancellationToken).ConfigureAwait(false);

        var principal = _seguranca.PrincipalId;
        if (string.IsNullOrWhiteSpace(principal) || !Guid.TryParse(principal, out var usuarioId))
        {
            // Autorização passou mas o principal não é um usuário resolvível — falha segura.
            throw new NaoAutenticadoException();
        }

        var agora = _relogio.AgoraUtc;
        var correlationId = ResolverCorrelationId();

        // (3) Nome e extensão derivados no SERVIDOR (o nome do cliente é apenas rótulo sanitizado).
        var nomeExibicao = GuardaCaminho.SanitizarNomeExibicao(comando.NomeOriginal);
        var extensao = GuardaCaminho.ExtrairExtensao(comando.NomeOriginal);

        await AuditarAsync(tenantId, principal, AcaoAuditoria.UploadStarted, ResultadoAuditoria.Permitido, agora,
            arquivoId: null, motivo: null, extensao: extensao, tamanho: null, sha256: null, cancellationToken).ConfigureAwait(false);

        // (4) Allowlist de extensão (a conferência final é contra o conteúdo real, adiante).
        if (!_opcoes.ExtensaoPermitida(extensao))
        {
            return await RejeitarAsync(tenantId, principal, "extensao_nao_permitida", extensao, agora, cancellationToken)
                .ConfigureAwait(false);
        }

        // (5) Streaming para área temporária privada, com hash SHA-256 e limite de tamanho.
        await using var temporario = await _areaTemporaria.CriarAsync(cancellationToken).ConfigureAwait(false);
        var copia = await CopiarComHashAsync(comando.Conteudo, temporario, _opcoes.MaxTamanhoBytes, cancellationToken)
            .ConfigureAwait(false);

        if (copia.Excedeu)
        {
            return await RejeitarAsync(tenantId, principal, "tamanho_excedido", extensao, agora, cancellationToken)
                .ConfigureAwait(false);
        }

        if (copia.Tamanho <= 0)
        {
            return await RejeitarAsync(tenantId, principal, "arquivo_vazio", extensao, agora, cancellationToken)
                .ConfigureAwait(false);
        }

        var tamanho = copia.Tamanho;
        var sha256 = copia.Sha256;

        // (6) Validação do CONTEÚDO real (magic bytes / anti ZIP bomb) — independente da extensão.
        temporario.Position = 0;
        var validacao = await _validadorConteudo.ValidarAsync(temporario, extensao, cancellationToken).ConfigureAwait(false);
        if (!validacao.Valido)
        {
            return await RejeitarAsync(tenantId, principal, "conteudo_invalido", extensao, agora, cancellationToken)
                .ConfigureAwait(false);
        }

        var contentType = validacao.ContentTypeDetectado;

        // (7) Idempotência tenant-scoped por hash (regra 12). Segunda barreira: unicidade no banco.
        if (await _repositorio.ObterIdPorHashAsync(sha256, cancellationToken).ConfigureAwait(false) is Guid existenteId)
        {
            await AuditarAsync(tenantId, principal, AcaoAuditoria.UploadDuplicated, ResultadoAuditoria.Permitido, agora,
                arquivoId: existenteId, motivo: null, extensao: extensao, tamanho: tamanho, sha256: sha256, cancellationToken)
                .ConfigureAwait(false);
            return ResultadoIngestao.Duplicado(existenteId);
        }

        // (8) Antivírus fail-closed. Nunca "considerar limpo" quando indisponível/erro.
        var scan = await EscanearAsync(temporario, cancellationToken).ConfigureAwait(false);

        if (scan == ResultadoScan.Infectado)
        {
            return await RegistrarInfectadoAsync(
                tenantId, principal, nomeExibicao, extensao, contentType, tamanho, sha256, usuarioId, correlationId,
                agora, cancellationToken).ConfigureAwait(false);
        }

        if (scan == ResultadoScan.Erro || (scan != ResultadoScan.Limpo && _opcoes.AntivirusObrigatorio))
        {
            // Scanner obrigatório e sem resultado limpo → rejeita (sem persistir, permitindo reenvio depois). Uma FALHA da
            // varredura rejeita mesmo quando o antivírus não é obrigatório: o arquivo nunca poderia ser processado
            // (ArquivoImportacao.ScanPermiteProcessar) e ficaria parado — melhor pedir o reenvio.
            return await RejeitarAsync(tenantId, principal, "verificacao_indisponivel", extensao, agora, cancellationToken)
                .ConfigureAwait(false);
        }

        // (9) Armazenamento em área privada tenant-scoped (nome GERADO, fora de wwwroot).
        var nomeSeguro = GuardaCaminho.GerarNome(extensao);
        var caminhoRelativo = $"{tenantId:N}/{nomeSeguro}";

        temporario.Position = 0;
        var referencia = await _armazenamento.SalvarAsync(caminhoRelativo, temporario, cancellationToken).ConfigureAwait(false);

        try
        {
            // (10) Metadados no banco do TENANT (nunca o conteúdo; nunca o catálogo).
            var expira = _retencao.ExpiraEm(CategoriaRetencao.ArquivosImportacao, agora);
            var arquivo = ArquivoImportacao.Registrar(
                nomeExibicao, nomeSeguro, extensao, contentType, tamanho, sha256, usuarioId, correlationId,
                (int)CategoriaRetencao.ArquivosImportacao, agora, expira);
            arquivo.RegistrarScan(scan, agora);
            arquivo.MarcarArmazenado(referencia, agora);

            await _repositorio.AdicionarAsync(arquivo, cancellationToken).ConfigureAwait(false);

            try
            {
                await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ViolacaoUnicidadeException)
            {
                // Corrida: outro upload do mesmo hash venceu. Idempotente — descarta o blob órfão.
                await RemoverSilenciosoAsync(referencia, cancellationToken).ConfigureAwait(false);
                await AuditarAsync(tenantId, principal, AcaoAuditoria.UploadDuplicated, ResultadoAuditoria.Permitido, agora,
                    arquivoId: null, motivo: null, extensao: extensao, tamanho: tamanho, sha256: sha256, cancellationToken)
                    .ConfigureAwait(false);
                return ResultadoIngestao.Duplicado(null);
            }

            // (11) Auditoria do desfecho de sucesso.
            await AuditarAsync(tenantId, principal, AcaoAuditoria.UploadStored, ResultadoAuditoria.Permitido, agora,
                arquivoId: arquivo.Id, motivo: null, extensao: extensao, tamanho: tamanho, sha256: sha256, cancellationToken)
                .ConfigureAwait(false);

            return ResultadoIngestao.Aceito(arquivo.Id);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException and not TenantContextAusenteException)
        {
            // Falha inesperada após armazenar → remove o blob e audita a falha (sem vazar detalhe).
            await RemoverSilenciosoAsync(referencia, cancellationToken).ConfigureAwait(false);
            await AuditarAsync(tenantId, principal, AcaoAuditoria.UploadFailed, ResultadoAuditoria.Negado, agora,
                arquivoId: null, motivo: "falha_persistencia", extensao: extensao, tamanho: tamanho, sha256: sha256,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ResultadoIngestao> RegistrarInfectadoAsync(
        Guid tenantId,
        string principal,
        string nomeExibicao,
        string extensao,
        string contentType,
        long tamanho,
        string sha256,
        Guid usuarioId,
        string correlationId,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        // Arquivo infectado NÃO é armazenado (o binário nunca vai para a área privada). Registra-se
        // apenas o metadado rejeitado (bloqueia reenvio pela unicidade de hash) e audita-se.
        var expira = _retencao.ExpiraEm(CategoriaRetencao.ArquivosImportacao, agora);
        var arquivo = ArquivoImportacao.Registrar(
            nomeExibicao, GuardaCaminho.GerarNome(extensao), extensao, contentType, tamanho, sha256, usuarioId,
            correlationId, (int)CategoriaRetencao.ArquivosImportacao, agora, expira);
        arquivo.RegistrarScan(ResultadoScan.Infectado, agora);
        arquivo.Rejeitar("arquivo_infectado", agora);

        try
        {
            await _repositorio.AdicionarAsync(arquivo, cancellationToken).ConfigureAwait(false);
            await _repositorio.SalvarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            // Já havia registro para este hash — idempotente.
        }

        await AuditarAsync(tenantId, principal, AcaoAuditoria.UploadInfected, ResultadoAuditoria.Negado, agora,
            arquivoId: null, motivo: "arquivo_infectado", extensao: extensao, tamanho: tamanho, sha256: sha256,
            cancellationToken).ConfigureAwait(false);

        return ResultadoIngestao.Rejeitado("Arquivo rejeitado na validação de segurança.");
    }

    private async ValueTask<ResultadoScan> EscanearAsync(Stream conteudo, CancellationToken cancellationToken)
    {
        if (!_antivirus.Disponivel)
        {
            return ResultadoScan.Indisponivel;
        }

        conteudo.Position = 0;
        var resultado = await _antivirus.EscanearAsync(conteudo, cancellationToken).ConfigureAwait(false);
        return resultado switch
        {
            ResultadoAntivirus.Limpo => ResultadoScan.Limpo,
            ResultadoAntivirus.Infectado => ResultadoScan.Infectado,
            ResultadoAntivirus.Indisponivel => ResultadoScan.Indisponivel,
            _ => ResultadoScan.Erro,
        };
    }

    private async Task<ResultadoIngestao> RejeitarAsync(
        Guid tenantId,
        string principal,
        string motivo,
        string extensao,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        await AuditarAsync(tenantId, principal, AcaoAuditoria.UploadRejected, ResultadoAuditoria.Negado, agora,
            arquivoId: null, motivo: motivo, extensao: extensao, tamanho: null, sha256: null, cancellationToken)
            .ConfigureAwait(false);
        return ResultadoIngestao.Rejeitado("Arquivo rejeitado na validação.");
    }

    private static async Task<CopiaHash> CopiarComHashAsync(
        Stream origem,
        Stream destino,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(TamanhoBuffer);
        long total = 0;
        try
        {
            int lidos;
            while ((lidos = await origem.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += lidos;
                if (total > maxBytes)
                {
                    return new CopiaHash(total, string.Empty, Excedeu: true);
                }

                hasher.AppendData(buffer, 0, lidos);
                await destino.WriteAsync(buffer.AsMemory(0, lidos), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await destino.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new CopiaHash(total, Convert.ToHexStringLower(hasher.GetHashAndReset()), Excedeu: false);
    }

    private async ValueTask RemoverSilenciosoAsync(string referencia, CancellationToken cancellationToken)
    {
        try
        {
            await _armazenamento.RemoverAsync(referencia, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // Limpeza best-effort de blob órfão — a falha aqui não deve mascarar o desfecho principal.
        }
    }

    private string ResolverCorrelationId()
    {
        var correlation = _correlacao.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlation))
        {
            return Guid.NewGuid().ToString("N");
        }

        return correlation.Length > 64 ? correlation[..64] : correlation;
    }

    private ValueTask AuditarAsync(
        Guid tenantId,
        string principal,
        string acao,
        ResultadoAuditoria resultado,
        DateTimeOffset agora,
        Guid? arquivoId,
        string? motivo,
        string? extensao,
        long? tamanho,
        string? sha256,
        CancellationToken cancellationToken)
    {
        var metadados = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(extensao))
        {
            metadados["extensao"] = extensao;
        }

        if (tamanho is long bytes)
        {
            metadados["tamanhoBytes"] = bytes.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(sha256))
        {
            metadados["sha256"] = sha256;
        }

        var evento = new EventoAuditoria(principal, tenantId.ToString(), acao, resultado, agora)
        {
            TipoRecurso = "ArquivoImportacao",
            RecursoId = arquivoId?.ToString(),
            Motivo = motivo,
            Metadados = metadados.Count > 0 ? metadados : null,
        };

        return _auditoria.RegistrarAsync(evento, cancellationToken);
    }

    private readonly record struct CopiaHash(long Tamanho, string Sha256, bool Excedeu);
}
