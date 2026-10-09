using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Casos;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;
using PortalBase.Shared.Resultados;

namespace PortalBase.Application.Catalogo;

/// <summary>
/// Entrada do caso de uso de criação de tenant. Carrega SOMENTE os campos de entrada;
/// não expõe Id, Ativo, nome físico da base nem outros atributos privilegiados — o
/// agregado os define no servidor (defesa contra mass assignment — seção 21). O <paramref name="Nome"/>
/// (ex.: STAKE) é validado/normalizado pelo domínio (<see cref="NomeTenant"/>) e só então define a base física
/// <c>portalbase_tenant_&lt;nome&gt;</c> — o nome físico nunca é aceito pronto da entrada.
/// <paramref name="SolicitanteId"/> é o principal autenticado (fornecido pela camada
/// de autorização — Prompt 04) usado apenas para auditoria.
/// </summary>
public sealed record CriarTenantComando(
    string SolicitanteId,
    string Cnpj,
    string Nome,
    string RazaoSocial,
    string ReferenciaSegredoConexao) : IComando<Guid>;

/// <summary>
/// Cria um tenant de forma controlada: valida CNPJ e dados, verifica unicidade
/// (primeira barreira) e confia na constraint única do banco (segunda barreira, contra
/// corrida — seção 25). Registra auditoria administrativa (regra 9).
/// </summary>
public sealed class CriarTenantManipulador : IManipuladorComando<CriarTenantComando, Guid>
{
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IRepositorioTenant _repositorio;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public CriarTenantManipulador(
        IAutorizacaoPlataforma autorizacao,
        IRepositorioTenant repositorio,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _autorizacao = autorizacao;
        _repositorio = repositorio;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    public async Task<Resultado<Guid>> ManipularAsync(CriarTenantComando comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comando);

        // AUTORIZAÇÃO EXPLÍCITA NO APLICATIVO (Prompt 71, P2-14): criar tenant é operação de PLATAFORMA
        // (não há tenant ainda). Deny-by-default: exige administrador de plataforma autenticado (+MFA quando
        // exigido). Fail-closed e AUDITADO — a barreira é aqui, não na UI/HTTP; nenhuma rota/botão/comando
        // consegue criar tenant sem passar por este gate. Preserva o contrato Resultado deste manipulador.
        var autz = await _autorizacao.AvaliarAsync(Permissoes.PlataformaTenantProvisionar, cancellationToken).ConfigureAwait(false);
        if (!autz.Permitido)
        {
            await _auditoria.RegistrarAsync(
                new EventoAuditoria(
                    string.IsNullOrWhiteSpace(comando.SolicitanteId) ? "anonimo" : comando.SolicitanteId,
                    "GLOBAL",
                    "TenantCriacaoNegada",
                    ResultadoAuditoria.Negado,
                    _relogio.AgoraUtc),
                cancellationToken).ConfigureAwait(false);

            return autz.Motivo == MotivoNegacao.NaoAutenticado
                ? Resultado.Falha<Guid>(Erro.NaoAutenticado("tenant.nao_autenticado", "Solicitante não autenticado."))
                : Resultado.Falha<Guid>(Erro.NaoAutorizado("tenant.nao_autorizado", "Operação de plataforma não autorizada."));
        }

        Cnpj cnpj;
        NomeTenant nome;
        Tenant tenant;
        try
        {
            cnpj = Cnpj.Criar(comando.Cnpj);
            nome = NomeTenant.Criar(comando.Nome);
            tenant = Tenant.Criar(
                cnpj,
                nome,
                comando.RazaoSocial,
                comando.ReferenciaSegredoConexao,
                _relogio.AgoraUtc);
        }
        catch (EntradaInvalidaException excecao)
        {
            return Resultado.Falha<Guid>(Erro.Validacao("tenant.dados_invalidos", excecao.Message));
        }

        if (await _repositorio.ExisteComCnpjAsync(cnpj, cancellationToken).ConfigureAwait(false))
        {
            return Resultado.Falha<Guid>(Erro.Conflito("tenant.cnpj_duplicado", "Já existe um tenant com este CNPJ."));
        }

        if (await _repositorio.ExisteComNomeAsync(nome, cancellationToken).ConfigureAwait(false))
        {
            return Resultado.Falha<Guid>(Erro.Conflito("tenant.nome_duplicado", $"Já existe um tenant com o nome {nome.Valor}."));
        }

        await _repositorio.AdicionarAsync(tenant, cancellationToken).ConfigureAwait(false);

        try
        {
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            // Corrida: outra requisição criou o mesmo CNPJ ou nome entre a verificação e o commit.
            return Resultado.Falha<Guid>(Erro.Conflito("tenant.duplicado", "Já existe um tenant com este CNPJ ou nome."));
        }

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(
                comando.SolicitanteId,
                tenant.Id.ToString(),
                "TenantCriado",
                ResultadoAuditoria.Permitido,
                _relogio.AgoraUtc),
            cancellationToken).ConfigureAwait(false);

        return Resultado.Ok(tenant.Id);
    }
}
