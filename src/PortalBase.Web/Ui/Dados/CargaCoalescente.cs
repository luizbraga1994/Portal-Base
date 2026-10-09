namespace PortalBase.Web.Ui.Dados;

/// <summary>
/// Coordena as cargas de uma tela no circuito do Blazor:
/// <list type="bullet">
/// <item><b>Uma por vez.</b> O DbContext do circuito não aceita duas operações simultâneas; duas consultas sobrepostas
/// davam "Não foi possível carregar" de forma intermitente (REG-001, auditoria de 08/10/2026).</item>
/// <item><b>Nenhum pedido se perde.</b> Um filtro, uma página ou uma recarga pedidos durante uma carga em andamento não
/// são descartados: a carga em curso repete a consulta com os valores ATUAIS da tela antes de terminar.</item>
/// <item><b>Só o resultado mais recente é exibido.</b> O resultado de uma consulta que ficou velha não é aplicado.</item>
/// </list>
/// Feito para o contexto de sincronização do circuito (uma continuação por vez), que é onde as telas rodam; não é
/// seguro para chamadas de threads diferentes ao mesmo tempo.
/// </summary>
public sealed class CargaCoalescente
{
    private bool _emAndamento;
    private bool _pendente;

    /// <summary>Há uma carga em andamento (para desativar botões e mostrar o indicador).</summary>
    public bool EmAndamento => _emAndamento;

    /// <summary>
    /// Pede uma carga. <paramref name="consultar"/> é chamado de novo a cada volta, então deve ler os filtros atuais da
    /// tela; <paramref name="aplicar"/> recebe só o resultado da consulta mais recente. Se já houver uma carga em
    /// andamento, retorna na hora: a carga em curso fará a consulta pedida.
    /// </summary>
    public async Task ExecutarAsync<T>(Func<Task<T>> consultar, Action<T> aplicar)
    {
        ArgumentNullException.ThrowIfNull(consultar);
        ArgumentNullException.ThrowIfNull(aplicar);

        _pendente = true;
        if (_emAndamento)
        {
            return;
        }

        _emAndamento = true;
        try
        {
            while (_pendente)
            {
                _pendente = false;
                var resultado = await consultar();
                if (!_pendente)
                {
                    aplicar(resultado);
                }
            }
        }
        finally
        {
            _pendente = false;
            _emAndamento = false;
        }
    }
}
