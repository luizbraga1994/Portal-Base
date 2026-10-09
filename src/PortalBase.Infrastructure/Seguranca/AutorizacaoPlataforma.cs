using System.Collections.Frozen;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Seguranca;

/// <summary>
/// Autorização de PLATAFORMA (Prompt 10.1) — deny-by-default. Concede operações GLOBAIS apenas a quem é
/// administrador de plataforma por uma de duas fontes do SERVIDOR: (1) o perfil "Administrador da plataforma"
/// gravado no catálogo (usuário ativo) — acesso total, SEM exigir o segundo fator; (2) a lista
/// <c>AutorizacaoPlataforma:AdministradoresPlataforma</c> da configuração — com o segundo fator quando
/// <c>ExigirMfa</c>. NUNCA usa tenant, papel de tenant, nem qualquer dado do cliente para decidir (regras 3, 4, 15).
/// Além disso, PRINCIPAIS DE SISTEMA (definidos só pelos hosts de trabalho, nunca por login) recebem uma lista
/// FIXA e mínima de permissões — ex.: o backup agendado só pode executar backup.
/// Toda negação é auditada; nenhum segredo é registrado.
/// </summary>
public sealed class AutorizacaoPlataforma : IAutorizacaoPlataforma
{
    /// <summary>Principal de sistema → permissões globais EXATAS (fixas no código; nada vem de configuração/cliente).</summary>
    private static readonly FrozenDictionary<string, FrozenSet<string>> PermissoesSistema =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            [Application.Backup.ExecucaoBackupAgendado.PrincipalSistema] = new[] { Permissoes.BackupExecutar }.ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly IContextoSeguranca _seguranca;
    private readonly IRepositorioUsuario _usuarios;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly FrozenSet<string> _administradores;
    private readonly bool _exigirMfa;

    public AutorizacaoPlataforma(
        IContextoSeguranca seguranca,
        IRepositorioUsuario usuarios,
        OpcoesAutorizacaoPlataforma opcoes,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _seguranca = seguranca;
        _usuarios = usuarios;
        _auditoria = auditoria;
        _relogio = relogio;
        _administradores = opcoes.AdministradoresPlataforma
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _exigirMfa = opcoes.ExigirMfa;
    }

    public async ValueTask<ResultadoAutorizacao> AvaliarAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissaoGlobal);
        return await AvaliarInternoAsync(permissaoGlobal, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ExigirAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissaoGlobal);

        var resultado = await AvaliarInternoAsync(permissaoGlobal, cancellationToken).ConfigureAwait(false);
        if (resultado.Permitido)
        {
            return;
        }

        var principal = _seguranca.PrincipalId ?? "anonimo";
        await _auditoria
            .RegistrarAsync(
                new EventoAuditoria(principal, "GLOBAL", AcaoAuditoria.PlataformaAcessoNegado, ResultadoAuditoria.Negado, _relogio.AgoraUtc)
                {
                    Motivo = $"Autz plataforma negada: {permissaoGlobal}",
                },
                cancellationToken)
            .ConfigureAwait(false);

        throw resultado.Motivo switch
        {
            MotivoNegacao.NaoAutenticado => new NaoAutenticadoException(),
            MotivoNegacao.MfaRequerido => new MfaRequeridoException(),
            _ => new AutorizacaoNegadaException(),
        };
    }

    private async ValueTask<ResultadoAutorizacao> AvaliarInternoAsync(string permissaoGlobal, CancellationToken cancellationToken)
    {
        if (!_seguranca.EstaAutenticado || string.IsNullOrWhiteSpace(_seguranca.PrincipalId))
        {
            return ResultadoAutorizacao.Negar(MotivoNegacao.NaoAutenticado);
        }

        // Principal de sistema: só as permissões fixas dele (comparação exata; qualquer outra é negada).
        if (PermissoesSistema.TryGetValue(_seguranca.PrincipalId, out var concedidas))
        {
            return concedidas.Contains(permissaoGlobal)
                ? ResultadoAutorizacao.Ok
                : ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao);
        }

        // Perfil "Administrador da plataforma" (catálogo, usuário ativo): acesso total, sem segundo fator.
        if (Guid.TryParse(_seguranca.PrincipalId, out var usuarioId)
            && await _usuarios.EhAdministradorPlataformaAsync(usuarioId, cancellationToken).ConfigureAwait(false))
        {
            return ResultadoAutorizacao.Ok;
        }

        // Deny-by-default: fora do perfil, só principais explicitamente listados na configuração.
        if (!_administradores.Contains(_seguranca.PrincipalId.Trim()))
        {
            return ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao);
        }

        if (_exigirMfa && !_seguranca.MfaSatisfeito)
        {
            return ResultadoAutorizacao.Negar(MotivoNegacao.MfaRequerido);
        }

        return ResultadoAutorizacao.Ok;
    }
}
