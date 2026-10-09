using MySql.Data.MySqlClient;

namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Configuração de acesso às bases de contribuinte (seções 19, 21). Separa a credencial
/// de RUNTIME (menor privilégio, DML nas bases ativas) da credencial ADMINISTRATIVA
/// (DDL/grants — provisionamento). As SENHAS não ficam aqui: apenas a REFERÊNCIA ao
/// segredo no cofre (regra 7). O nome da base nunca vem daqui nem do cliente — é derivado
/// do identificador do tenant no catálogo.
/// </summary>
public sealed class OpcoesBancoTenant
{
    public const string Secao = "BancoTenant";

    public string Servidor { get; set; } = "localhost";

    public uint Porta { get; set; } = 3306;

    /// <summary>Usuário MySQL de runtime (a aplicação). Recebe apenas DML por base ativa.</summary>
    public string RuntimeUsuario { get; set; } = string.Empty;

    /// <summary>Nome do segredo (no cofre) com a senha de runtime — nunca a senha em si.</summary>
    public string RuntimeSenhaRef { get; set; } = string.Empty;

    /// <summary>Usuário MySQL administrativo (provisionamento: CREATE DATABASE/GRANT).</summary>
    public string AdminUsuario { get; set; } = string.Empty;

    public string AdminSenhaRef { get; set; } = string.Empty;

    // Pool por tenant (cada base tem seu pool; dimensionar para o nº de contribuintes).
    public uint MaximoPool { get; set; } = 20;

    public uint MinimoPool { get; set; }

    public uint VidaConexaoSegundos { get; set; } = 300;

    public uint TimeoutConexaoSegundos { get; set; } = 15;

    /// <summary>
    /// Modo TLS da conexão MySQL (Prompt 48, Fase 18 — fecha o gap do P35 <c>SslMode=Preferred</c>). Padrão
    /// SEGURO <see cref="MySqlSslMode.Required"/>: EXIGE TLS (sem downgrade silencioso para texto claro).
    /// Em produção, com CA/certificado provisionados, prefira <c>VerifyCA</c>/<c>VerifyFull</c> (config-driven).
    /// Um MySQL local de teste sem TLS pode usar <c>Disabled</c> explicitamente na configuração de teste.
    /// </summary>
    public MySqlSslMode SslMode { get; set; } = MySqlSslMode.Required;

    /// <summary>
    /// Permite ao cliente recuperar a chave pública RSA do servidor para o handshake
    /// <c>caching_sha2_password</c> em conexão NÃO-TLS. Padrão <b>false</b> (seguro): em produção a conexão é
    /// TLS (<see cref="SslMode"/> Required/VerifyCA) e o <c>caching_sha2</c> autentica sem recuperar chave —
    /// habilitar isto sobre texto claro seria risco de MITM. É ligado SOMENTE em ambiente de teste/CI cujo
    /// MySQL efêmero não tem TLS (serviço <c>mysql:8.4</c>, onde <c>native_password</c> vem desligado), para
    /// que o usuário de runtime recém-criado consiga conectar sobre não-TLS. NUNCA habilitar em produção.
    /// </summary>
    public bool PermitirRecuperacaoChavePublica { get; set; }
}
