namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Ciclo de vida operacional do contribuinte (Prompt 09). Distingue a INFRAESTRUTURA pronta
/// (<see cref="Pronto"/> — base criada, migrada e validada) da AUTORIZAÇÃO funcional de uso
/// (<see cref="Ativo"/> — único estado que permite operação). Nunca é definido pelo
/// cliente: toda transição é server-side. Substitui um simples <c>bool Ativo</c>.
///
/// Transições válidas:
/// Pendente → Provisionando → (Pronto | Falhado);
/// Falhado → Provisionando (reprocesso);
/// Pronto → Ativo (ativação);
/// Ativo → Desabilitado (desabilitação);
/// Desabilitado → Ativo (reativação, quando permitida);
/// Pronto/Ativo → Desabilitado; qualquer estado provisionado permanece com base preservada.
/// </summary>
public enum EstadoProvisionamento
{
    /// <summary>Registrado no catálogo; base ainda não provisionada.</summary>
    Pendente = 1,

    /// <summary>Provisionamento em andamento (criação de base, migrations, grants).</summary>
    Provisionando = 2,

    /// <summary>Base provisionada, migrada e VALIDADA — pronta, porém ainda NÃO operacional.</summary>
    Pronto = 3,

    /// <summary>Falha de provisionamento; NÃO operacional até reprocessar com sucesso.</summary>
    Falhado = 4,

    /// <summary>Tenant desabilitado; bloqueado para toda operação (base preservada).</summary>
    Desabilitado = 5,

    /// <summary>Tenant ATIVO — único estado que autoriza operação funcional.</summary>
    Ativo = 6,
}
