namespace PortalBase.Application.Notificacoes;

/// <summary>
/// E-mail transacional (ex.: link de redefinição de senha). Pode carregar valor de uso único: NUNCA é registrado em
/// log nem persistido (regra 8) — só trafega da fila para o transporte.
/// </summary>
public sealed record MensagemEmail(string Para, string Assunto, string Texto, string? Html = null);

/// <summary>
/// Transporte do e-mail: o servidor SMTP configurado ou, só em Development, uma pasta local. Lança em falha — quem
/// dispara decide a nova tentativa (regra 11). Recebe <see cref="CancellationToken"/> e aplica timeout próprio.
/// </summary>
public interface IEnviadorEmail
{
    Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fila de disparo: o caso de uso ENFILEIRA e responde na hora, sem esperar o servidor de e-mail — o tempo de
/// resposta não depende de o e-mail existir (anti-enumeração) nem de o SMTP estar lento. O envio, com nova tentativa
/// controlada, é feito em segundo plano.
/// </summary>
public interface IFilaEmail
{
    /// <summary>Há transporte configurado (Email:Modo diferente de Desligado)?</summary>
    bool Habilitado { get; }

    /// <summary>Enfileira para envio. <c>false</c> se o envio está desligado ou a fila está cheia (nada é perdido em silêncio: o motivo vai para o log, sem o conteúdo).</summary>
    bool Enfileirar(MensagemEmail mensagem);
}

/// <summary>
/// Contadores da fila de disparo (FILA-001), sem conteúdo nem destinatário: quantos e-mails NÃO foram enviados desde
/// que o processo subiu (falha final do servidor de e-mail, fila cheia ou perdidos no desligamento) e quantos aguardam.
/// A rotina de alertas compara o total entre uma verificação e outra.
/// </summary>
public interface IEstatisticasFilaEmail
{
    long NaoEnviados { get; }

    int Pendentes { get; }
}
