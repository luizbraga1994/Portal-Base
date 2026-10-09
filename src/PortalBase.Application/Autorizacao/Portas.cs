using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Autorizacao;

/// <summary>
/// Contexto de segurança da requisição, resolvido NO SERVIDOR a partir da sessão
/// autenticada (nunca do estado de componente Blazor — seções 9, 42). Escopo de
/// requisição. Deny-by-default: anônimo não autenticado, MFA não satisfeito.
/// </summary>
public interface IContextoSeguranca
{
    bool EstaAutenticado { get; }

    /// <summary>Identificador do principal autenticado (string), ou <c>null</c> se anônimo.</summary>
    string? PrincipalId { get; }

    /// <summary>Segundo fator satisfeito NESTA sessão (step-up efetuado). Falso por padrão.</summary>
    bool MfaSatisfeito { get; }
}

/// <summary>Face mutável do contexto de segurança, escrita apenas pela camada de apresentação/host.</summary>
public interface IContextoSegurancaMutavel : IContextoSeguranca
{
    void Definir(string principalId, bool mfaSatisfeito);

    void Limpar();
}

/// <summary>
/// Resultado da verificação de senha. <see cref="SucessoPrecisaRehash"/> sinaliza que o
/// hash usa parâmetros antigos e deve ser regravado (rotação de custo), sem falhar o login.
/// </summary>
public enum ResultadoVerificacaoSenha
{
    Falha = 0,
    Sucesso = 1,
    SucessoPrecisaRehash = 2,
}

/// <summary>
/// Hashing de senha (seção 5/6). Implementado com primitiva vetada (PBKDF2 do ASP.NET Core
/// Identity) — NUNCA algoritmo próprio. A senha em claro nunca é persistida nem registrada.
/// </summary>
public interface IHasherSenha
{
    string Gerar(string senha);

    ResultadoVerificacaoSenha Verificar(string hashArmazenado, string senhaInformada);
}

/// <summary>
/// Provedor TOTP (RFC 6238) para o segundo fator (seção 18). Gera o segredo, valida o
/// código dentro de uma janela de tolerância e monta a URI de provisionamento (otpauth://).
/// NUNCA registra o segredo nem o código.
/// </summary>
public interface IProvedorTotp
{
    /// <summary>Gera um novo segredo aleatório (Base32) para o autenticador.</summary>
    string GerarSegredo();

    /// <summary>Valida um código TOTP contra o segredo em claro, com janela de tolerância.</summary>
    bool Validar(string segredoBase32, string codigo, DateTimeOffset agoraUtc);

    /// <summary>
    /// Valida como <see cref="Validar"/>, mas devolve o PASSO (contador Unix/30s) que casou — ou <c>null</c> se
    /// inválido. O passo é a chave de ANTI-REPLAY (Prompt 68): o step-up marca o passo como consumido para que o
    /// MESMO código não seja aceito de novo dentro da janela de validade (~30-90s).
    /// </summary>
    long? ValidarObtendoPasso(string segredoBase32, string codigo, DateTimeOffset agoraUtc);

    /// <summary>URI otpauth:// para provisionar o app autenticador (QR code). Não contém senha.</summary>
    string GerarUriProvisionamento(string segredoBase32, string emissor, string conta);
}

/// <summary>
/// ANTI-REPLAY de TOTP (Prompt 68, seção 5/18) — impede a reutilização de um código já consumido dentro da sua
/// janela de validade. Um código TOTP é válido por ~30-90s; sem este guarda, um código capturado poderia ser
/// reenviado e aceito novamente. Registra, POR USUÁRIO, o maior passo (contador) já consumido e rejeita passos
/// menores ou iguais. Nunca guarda o segredo nem o código — apenas o número do passo. É defesa DENTRO do
/// processo (in-memory); para topologia multi-instância, uma implementação distribuída pode substituir esta.
/// </summary>
public interface IRegistroConsumoTotp
{
    /// <summary>
    /// Tenta CONSUMIR o passo para o usuário. Retorna <c>true</c> e registra o passo se ele for MAIOR que o
    /// último consumido; retorna <c>false</c> (replay) se for igual/menor. Fail-closed por natureza.
    /// </summary>
    bool TentarConsumir(Guid usuarioId, long passo, DateTimeOffset agoraUtc);
}

/// <summary>
/// Proteção do segredo MFA em repouso (seção 18): cifra/decifra o segredo TOTP com uma
/// chave guardada no cofre. Assim o banco NUNCA guarda o segredo em claro. AEAD autenticado.
/// </summary>
public interface IProtetorSegredoMfa
{
    ValueTask<string> ProtegerAsync(string segredoClaro, CancellationToken cancellationToken = default);

    ValueTask<string> DesprotegerAsync(string segredoProtegido, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hash DETERMINÍSTICO (HMAC com chave do cofre) para valores de uso único que precisam ser
/// procurados por igualdade — tokens de recuperação e códigos MFA (seções 27, 36). Diferente
/// do hash de senha (salgado): aqui é preciso localizar o registro pelo hash.
/// </summary>
public interface IHashDeterministico
{
    ValueTask<string> HashAsync(string valor, CancellationToken cancellationToken = default);
}

/// <summary>
/// Gerador de valores aleatórios de alta entropia (fonte criptográfica) para tokens de
/// recuperação e códigos MFA (seções 27, 36). NUNCA usa RNG não-criptográfico.
/// </summary>
public interface IGeradorTokens
{
    /// <summary>Token url-safe de alta entropia (recuperação de conta).</summary>
    string GerarToken();

    /// <summary>Código de recuperação digitável (uso único), sem ambiguidade de caracteres.</summary>
    string GerarCodigoRecuperacao();

    /// <summary>
    /// Senha TEMPORÁRIA forte (16 caracteres: minúsculas, maiúsculas, números e símbolos, sem caracteres ambíguos),
    /// enviada por e-mail e trocada pelo usuário no primeiro acesso.
    /// </summary>
    string GerarSenhaTemporaria();
}

/// <summary>
/// Acesso ao usuário e às suas permissões efetivas por tenant, no catálogo central. As
/// permissões vêm SEMPRE do servidor (vínculo ativo + papéis escopados), nunca do cliente.
/// </summary>
public interface IRepositorioUsuario
{
    Task<Usuario?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Usuario?> ObterPorLoginAsync(string login, CancellationToken cancellationToken = default);

    /// <summary>Usuário cujo e-mail (o identificador do LOGIN) é este, comparado na forma canônica (<see cref="Usuario.NormalizarEmail"/>).</summary>
    Task<Usuario?> ObterPorEmailAsync(string email, CancellationToken cancellationToken = default);

    Task AdicionarAsync(Usuario usuario, CancellationToken cancellationToken = default);

    /// <summary>Permissões efetivas do usuário no tenant. Vazio se não houver vínculo ATIVO.</summary>
    Task<PermissoesTenant> ObterPermissoesAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>O usuário está ATIVO e tem o perfil "Administrador da plataforma"? (lido do catálogo, nunca do cliente)</summary>
    Task<bool> EhAdministradorPlataformaAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Existe ALGUM usuário (em qualquer situação) com o perfil "Administrador da plataforma"?</summary>
    Task<bool> ExisteAdministradorPlataformaAsync(CancellationToken cancellationToken = default);

    Task AdicionarCodigosRecuperacaoMfaAsync(IEnumerable<CodigoRecuperacaoMfa> codigos, CancellationToken cancellationToken = default);

    /// <summary>Apaga TODOS os códigos de recuperação do MFA do usuário (remoção do MFA). Devolve quantos apagou.</summary>
    Task<int> RemoverCodigosRecuperacaoMfaAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    Task<CodigoRecuperacaoMfa?> ObterCodigoRecuperacaoMfaAsync(Guid usuarioId, string hashCodigo, CancellationToken cancellationToken = default);

    Task AdicionarTokenRecuperacaoAsync(TokenRecuperacaoConta token, CancellationToken cancellationToken = default);

    Task<TokenRecuperacaoConta?> ObterTokenRecuperacaoAsync(string hashToken, CancellationToken cancellationToken = default);

    /// <summary>Quantos tokens de recuperação foram emitidos para o usuário desde o instante dado (limite anti-abuso).</summary>
    Task<int> ContarTokensRecuperacaoDesdeAsync(Guid usuarioId, DateTimeOffset desde, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consome o token de forma ATÔMICA no banco (só se ainda não usado e não expirado). <c>false</c> se outro pedido
    /// já o consumiu — dois envios simultâneos do mesmo link não redefinem a senha duas vezes.
    /// </summary>
    Task<bool> ConsumirTokenRecuperacaoAsync(Guid tokenId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default);

    /// <summary>Invalida (marca como usados) todos os tokens ainda abertos do usuário.</summary>
    Task InvalidarTokensRecuperacaoAsync(Guid usuarioId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// Gestão de acesso (vínculos usuário×tenant e papéis escopados — seção 21). Toda operação
/// aqui é privilegiada: o chamador (caso de uso admin) valida autorização antes.
/// </summary>
public interface IRepositorioAcesso
{
    Task<UsuarioTenant?> ObterVinculoAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default);

    Task AdicionarVinculoAsync(UsuarioTenant vinculo, CancellationToken cancellationToken = default);

    Task<bool> PapelDoVinculoExisteAsync(Guid usuarioTenantId, Guid papelId, CancellationToken cancellationToken = default);

    Task AdicionarPapelDoVinculoAsync(UsuarioTenantPapel papel, CancellationToken cancellationToken = default);

    /// <summary>Resolve um papel conhecido (por nome) para a entidade persistida no catálogo.</summary>
    Task<Papel?> ResolverPapelAsync(PapelConhecido papel, CancellationToken cancellationToken = default);
}
