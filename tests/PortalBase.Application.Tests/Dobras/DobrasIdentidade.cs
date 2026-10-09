using PortalBase.Application.Autorizacao;
using PortalBase.Application.Identidade;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Dobras;

/// <summary>Autorização de tenant configurável: allow-list de (principal, tenant).</summary>
internal sealed class AutorizacaoTenantFakeIdentidade : IAutorizacaoTenant
{
    public HashSet<(string Principal, Guid Tenant)> Permitidos { get; } = [];

    public ValueTask<bool> PodeAcessarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Permitidos.Contains((principalId, tenantId)));

    public ValueTask<bool> PodeAdministrarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Permitidos.Contains((principalId, tenantId)));
}

/// <summary>Repositório de usuários em memória, com permissões efetivas por (usuário, tenant).</summary>
internal sealed class RepositorioUsuarioFake : IRepositorioUsuario
{
    public List<Usuario> Usuarios { get; } = [];

    public Dictionary<(Guid Usuario, Guid Tenant), HashSet<string>> Permissoes { get; } = [];

    public List<CodigoRecuperacaoMfa> CodigosMfa { get; } = [];

    public List<TokenRecuperacaoConta> Tokens { get; } = [];

    public Task<Usuario?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Usuarios.Find(u => u.Id == id));

    public Task<Usuario?> ObterPorLoginAsync(string login, CancellationToken cancellationToken = default)
        => Task.FromResult(Usuarios.Find(u => u.Login == login));

    // Mesma semântica do repositório real: comparação na forma canônica (o catálogo compara sensível a caixa).
    public Task<Usuario?> ObterPorEmailAsync(string email, CancellationToken cancellationToken = default)
        => Task.FromResult(Usuarios.Find(u => string.Equals(u.Email, Usuario.NormalizarEmail(email), StringComparison.Ordinal)));

    public Task AdicionarAsync(Usuario usuario, CancellationToken cancellationToken = default)
    {
        Usuarios.Add(usuario);
        return Task.CompletedTask;
    }

    public Task<PermissoesTenant> ObterPermissoesAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(Permissoes.TryGetValue((usuarioId, tenantId), out var chaves)
            ? new PermissoesTenant(chaves)
            : PermissoesTenant.Nenhuma);

    public Task<bool> EhAdministradorPlataformaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => Task.FromResult(Usuarios.Any(u => u.Id == usuarioId && u.AdministradorPlataforma && u.Status == StatusUsuario.Ativo));

    public Task<bool> ExisteAdministradorPlataformaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Usuarios.Any(u => u.AdministradorPlataforma));

    public Task AdicionarCodigosRecuperacaoMfaAsync(IEnumerable<CodigoRecuperacaoMfa> codigos, CancellationToken cancellationToken = default)
    {
        CodigosMfa.AddRange(codigos);
        return Task.CompletedTask;
    }

    public Task<int> RemoverCodigosRecuperacaoMfaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => Task.FromResult(CodigosMfa.RemoveAll(c => c.UsuarioId == usuarioId));

    public Task<CodigoRecuperacaoMfa?> ObterCodigoRecuperacaoMfaAsync(Guid usuarioId, string hashCodigo, CancellationToken cancellationToken = default)
        => Task.FromResult(CodigosMfa.Find(c => c.UsuarioId == usuarioId && c.HashCodigo == hashCodigo));

    public Task AdicionarTokenRecuperacaoAsync(TokenRecuperacaoConta token, CancellationToken cancellationToken = default)
    {
        Tokens.Add(token);
        return Task.CompletedTask;
    }

    public Task<TokenRecuperacaoConta?> ObterTokenRecuperacaoAsync(string hashToken, CancellationToken cancellationToken = default)
        => Task.FromResult(Tokens.Find(t => t.HashToken == hashToken));

    public Task<int> ContarTokensRecuperacaoDesdeAsync(Guid usuarioId, DateTimeOffset desde, CancellationToken cancellationToken = default)
        => Task.FromResult(Tokens.Count(t => t.UsuarioId == usuarioId && t.CriadoEmUtc >= desde));

    // Mesma semântica do UPDATE condicional do repositório real: só consome se ainda válido.
    public Task<bool> ConsumirTokenRecuperacaoAsync(Guid tokenId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
    {
        var token = Tokens.Find(t => t.Id == tokenId);
        if (token is null || !token.EstaValido(agoraUtc))
        {
            return Task.FromResult(false);
        }

        token.Consumir(agoraUtc);
        return Task.FromResult(true);
    }

    public Task InvalidarTokensRecuperacaoAsync(Guid usuarioId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
    {
        foreach (var token in Tokens.Where(t => t.UsuarioId == usuarioId && t.EstaValido(agoraUtc)))
        {
            token.Consumir(agoraUtc);
        }

        return Task.CompletedTask;
    }

    public void DefinirPermissoes(Guid usuario, Guid tenant, params string[] chaves)
        => Permissoes[(usuario, tenant)] = [.. chaves];
}

/// <summary>Repositório de acesso em memória (vínculos e papéis).</summary>
internal sealed class RepositorioAcessoFake : IRepositorioAcesso
{
    public List<UsuarioTenant> Vinculos { get; } = [];

    public List<UsuarioTenantPapel> Papeis { get; } = [];

    public Task<UsuarioTenant?> ObterVinculoAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(Vinculos.Find(v => v.UsuarioId == usuarioId && v.TenantId == tenantId));

    public Task AdicionarVinculoAsync(UsuarioTenant vinculo, CancellationToken cancellationToken = default)
    {
        Vinculos.Add(vinculo);
        return Task.CompletedTask;
    }

    public Task<bool> PapelDoVinculoExisteAsync(Guid usuarioTenantId, Guid papelId, CancellationToken cancellationToken = default)
        => Task.FromResult(Papeis.Exists(p => p.UsuarioTenantId == usuarioTenantId && p.PapelId == papelId));

    public Task AdicionarPapelDoVinculoAsync(UsuarioTenantPapel papel, CancellationToken cancellationToken = default)
    {
        Papeis.Add(papel);
        return Task.CompletedTask;
    }

    public Task<Papel?> ResolverPapelAsync(PapelConhecido papel, CancellationToken cancellationToken = default)
        => Task.FromResult<Papel?>(Papel.Criar(Guid.NewGuid(), papel.ToString(), papel.ToString()));
}

/// <summary>Hasher de teste (NÃO criptográfico): compara igualdade prefixada. Nunca em produção.</summary>
internal sealed class HasherSenhaFake : IHasherSenha
{
    public string Gerar(string senha) => "H:" + senha;

    public ResultadoVerificacaoSenha Verificar(string hashArmazenado, string senhaInformada)
        => hashArmazenado == "H:" + senhaInformada
            ? ResultadoVerificacaoSenha.Sucesso
            : ResultadoVerificacaoSenha.Falha;
}

/// <summary>TOTP de teste: aceita um código fixo configurável. O passo deriva do relógio (30s), como o real.</summary>
internal sealed class ProvedorTotpFake : IProvedorTotp
{
    public string CodigoValido { get; set; } = "123456";

    public string GerarSegredo() => "SEGREDO";

    public bool Validar(string segredoBase32, string codigo, DateTimeOffset agoraUtc)
        => ValidarObtendoPasso(segredoBase32, codigo, agoraUtc) is not null;

    public long? ValidarObtendoPasso(string segredoBase32, string codigo, DateTimeOffset agoraUtc)
        => codigo == CodigoValido ? agoraUtc.ToUnixTimeSeconds() / 30 : null;

    public string GerarUriProvisionamento(string segredoBase32, string emissor, string conta) => "otpauth://totp/teste";
}

/// <summary>Anti-replay de TOTP em teste (mesma regra do real): rejeita passo já consumido para o usuário.</summary>
internal sealed class RegistroConsumoTotpFake : IRegistroConsumoTotp
{
    private readonly Dictionary<Guid, long> _ultimoPasso = new();

    public bool TentarConsumir(Guid usuarioId, long passo, DateTimeOffset agoraUtc)
    {
        if (_ultimoPasso.TryGetValue(usuarioId, out var ultimo) && passo <= ultimo)
        {
            return false;
        }

        _ultimoPasso[usuarioId] = passo;
        return true;
    }
}

/// <summary>Protetor de teste (reversível, NÃO criptográfico). Nunca em produção.</summary>
internal sealed class ProtetorSegredoMfaFake : IProtetorSegredoMfa
{
    public ValueTask<string> ProtegerAsync(string segredoClaro, CancellationToken cancellationToken = default)
        => ValueTask.FromResult("P:" + segredoClaro);

    public ValueTask<string> DesprotegerAsync(string segredoProtegido, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(segredoProtegido.StartsWith("P:", StringComparison.Ordinal) ? segredoProtegido[2..] : segredoProtegido);
}

/// <summary>Hash determinístico de teste.</summary>
internal sealed class HashDeterministicoFake : IHashDeterministico
{
    public ValueTask<string> HashAsync(string valor, CancellationToken cancellationToken = default)
        => ValueTask.FromResult("D:" + valor);
}

/// <summary>Gerador de tokens determinístico para testes.</summary>
internal sealed class GeradorTokensFake : IGeradorTokens
{
    private int _n;

    public string GerarToken() => "token-" + _n++;

    public string GerarCodigoRecuperacao() => "COD" + _n++;

    public string GerarSenhaTemporaria() => "Temp#Senha-" + _n++ + "Xy9";
}
