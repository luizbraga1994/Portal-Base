using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Cadastro central do contribuinte (IDENTIDADE do tenant), no banco de CATÁLOGO.
/// Guarda somente identidade e METADADOS OPERACIONAIS para localizar/administrar a
/// base individual — NUNCA dados contábeis nem credenciais (a conexão é referenciada
/// por <see cref="NomeBaseFisica"/> + <see cref="ReferenciaSegredoConexao"/>).
///
/// Encapsulado: sem setters públicos e sem construtor público. O ciclo de vida usa
/// <see cref="EstadoProvisionamento"/> (não um bool) — só <see cref="EstaOperacional"/>
/// permite operação. Transições atualizam o <see cref="ConcurrencyStamp"/> (concorrência
/// otimista, segunda barreira além das constraints).
/// </summary>
public sealed class Tenant
{
    private const int LimiteRazaoSocial = 200;

    /// <summary>Tamanho máximo da razão social (para as telas).</summary>
    public const int TamanhoMaximoRazaoSocial = LimiteRazaoSocial;
    private const int LimiteReferencia = 200;
    private const int LimiteVersao = 200;
    private const int LimiteErro = 512;

    private Tenant()
    {
    }

    private Tenant(
        Guid id,
        Cnpj cnpj,
        NomeTenant nome,
        string razaoSocial,
        string nomeBaseFisica,
        string referenciaSegredoConexao,
        DateTimeOffset agoraUtc)
    {
        Id = id;
        Cnpj = cnpj;
        Nome = nome.Valor;
        RazaoSocial = razaoSocial;
        NomeBaseFisica = nomeBaseFisica;
        ReferenciaSegredoConexao = referenciaSegredoConexao;
        Estado = EstadoProvisionamento.Pendente;
        CriadoEmUtc = agoraUtc;
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    public Cnpj Cnpj { get; private set; } = null!;

    /// <summary>Nome escolhido na criação (MAIÚSCULO, único, imutável) — o que a seleção de contribuinte mostra.</summary>
    public string Nome { get; private set; } = null!;

    public string RazaoSocial { get; private set; } = null!;

    /// <summary>Estado do ciclo de provisionamento — só <see cref="EstadoProvisionamento.Pronto"/> opera.</summary>
    public EstadoProvisionamento Estado { get; private set; }

    /// <summary>Nome físico (derivado do <see cref="Nome"/> e sanitizado) da base individual do contribuinte.</summary>
    public string NomeBaseFisica { get; private set; } = null!;

    /// <summary>Referência (nome no cofre) do segredo da conexão da base — nunca a credencial (regra 7).</summary>
    public string ReferenciaSegredoConexao { get; private set; } = null!;

    /// <summary>Versão de schema (última migration aplicada) da base; nulo até provisionar.</summary>
    public string? SchemaVersion { get; private set; }

    public string? UltimaMigracao { get; private set; }

    /// <summary>Último erro operacional (sanitizado, sem segredos/connection string).</summary>
    public string? UltimoErro { get; private set; }

    public DateTimeOffset CriadoEmUtc { get; private set; }

    public DateTimeOffset AtualizadoEmUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    /// <summary>
    /// Único estado que autoriza operação funcional (Prompt 09): <see cref="EstadoProvisionamento.Ativo"/>.
    /// Um tenant apenas <see cref="EstadoProvisionamento.Pronto"/> tem infraestrutura validada, mas ainda
    /// NÃO é operacional até a ativação.
    /// </summary>
    public bool EstaOperacional => Estado == EstadoProvisionamento.Ativo;

    /// <summary>Infraestrutura provisionada e validada (Pronto ou Ativo) — possui base física correspondente.</summary>
    public bool EstaProvisionado => Estado is EstadoProvisionamento.Pronto or EstadoProvisionamento.Ativo;

    public static Tenant Criar(
        Cnpj cnpj,
        NomeTenant nome,
        string razaoSocial,
        string referenciaSegredoConexao,
        DateTimeOffset agoraUtc,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(cnpj);
        ArgumentNullException.ThrowIfNull(nome);

        var identificador = id ?? Guid.NewGuid();

        return new Tenant(
            identificador,
            cnpj,
            nome,
            Exigir.TextoObrigatorioComLimite(razaoSocial, LimiteRazaoSocial, nameof(RazaoSocial)),
            NomeBaseFisicaTenant.Derivar(nome),
            Exigir.TextoObrigatorioComLimite(referenciaSegredoConexao, LimiteReferencia, nameof(ReferenciaSegredoConexao)),
            agoraUtc);
    }

    /// <summary>Inicia o provisionamento (de Pendente ou Falhado). Idempotente se já estiver Provisionando.</summary>
    public void IniciarProvisionamento(DateTimeOffset agoraUtc)
    {
        if (Estado == EstadoProvisionamento.Provisionando)
        {
            return;
        }

        if (Estado is not (EstadoProvisionamento.Pendente or EstadoProvisionamento.Falhado))
        {
            throw new EstadoInvalidoException(
                $"Não é possível iniciar provisionamento a partir do estado '{Estado}'.");
        }

        Estado = EstadoProvisionamento.Provisionando;
        UltimoErro = null;
        Tocar(agoraUtc);
    }

    /// <summary>Conclui o provisionamento com sucesso (de Provisionando para Pronto).</summary>
    public void ConcluirProvisionamento(string schemaVersion, string ultimaMigracao, DateTimeOffset agoraUtc)
    {
        Estado = EstadoProvisionamento.Pronto;
        SchemaVersion = Exigir.TextoObrigatorioComLimite(schemaVersion, LimiteVersao, nameof(SchemaVersion));
        UltimaMigracao = Exigir.TextoObrigatorioComLimite(ultimaMigracao, LimiteVersao, nameof(UltimaMigracao));
        UltimoErro = null;
        Tocar(agoraUtc);
    }

    /// <summary>Marca falha de provisionamento; o tenant permanece NÃO operacional.</summary>
    public void FalharProvisionamento(string erroSanitizado, DateTimeOffset agoraUtc)
    {
        Estado = EstadoProvisionamento.Falhado;
        UltimoErro = Truncar(Exigir.TextoObrigatorio(erroSanitizado, nameof(UltimoErro)), LimiteErro);
        Tocar(agoraUtc);
    }

    /// <summary>Atualiza a versão de schema após uma migration bem-sucedida em base já Pronta.</summary>
    public void RegistrarVersaoSchema(string schemaVersion, string ultimaMigracao, DateTimeOffset agoraUtc)
    {
        SchemaVersion = Exigir.TextoObrigatorioComLimite(schemaVersion, LimiteVersao, nameof(SchemaVersion));
        UltimaMigracao = Exigir.TextoObrigatorioComLimite(ultimaMigracao, LimiteVersao, nameof(UltimaMigracao));
        Tocar(agoraUtc);
    }

    /// <summary>
    /// Registra falha de atualização de schema em um tenant JÁ operacional (mantém
    /// <see cref="EstadoProvisionamento.Pronto"/> na versão anterior; rastreia o erro — seção 17).
    /// </summary>
    public void RegistrarFalhaMigracao(string erroSanitizado, DateTimeOffset agoraUtc)
    {
        UltimoErro = Truncar(Exigir.TextoObrigatorio(erroSanitizado, nameof(UltimoErro)), LimiteErro);
        Tocar(agoraUtc);
    }

    /// <summary>
    /// Ativa um tenant PRONTO (infraestrutura validada) para uso funcional (Prompt 09, seção 4).
    /// Idempotente se já <see cref="EstadoProvisionamento.Ativo"/>. Só a partir de
    /// <see cref="EstadoProvisionamento.Pronto"/> — reativação de um desabilitado usa <see cref="Reativar"/>.
    /// </summary>
    public void Ativar(DateTimeOffset agoraUtc)
    {
        if (Estado == EstadoProvisionamento.Ativo)
        {
            return;
        }

        if (Estado != EstadoProvisionamento.Pronto)
        {
            throw new EstadoInvalidoException(
                $"Só um tenant pronto pode ser ativado (estado atual: '{Estado}').");
        }

        Estado = EstadoProvisionamento.Ativo;
        Tocar(agoraUtc);
    }

    /// <summary>
    /// Reativa um tenant DESABILITADO (Prompt 09, seção 22). Idempotente se já
    /// <see cref="EstadoProvisionamento.Ativo"/>. Só a partir de <see cref="EstadoProvisionamento.Desabilitado"/>;
    /// a validação de infraestrutura é responsabilidade do caso de uso antes de chamar.
    /// </summary>
    public void Reativar(DateTimeOffset agoraUtc)
    {
        if (Estado == EstadoProvisionamento.Ativo)
        {
            return;
        }

        if (Estado != EstadoProvisionamento.Desabilitado)
        {
            throw new EstadoInvalidoException(
                $"Só um tenant desabilitado pode ser reativado (estado atual: '{Estado}').");
        }

        Estado = EstadoProvisionamento.Ativo;
        Tocar(agoraUtc);
    }

    /// <summary>
    /// Desabilita o tenant (Prompt 09, seção 21): bloqueia toda operação. A base NÃO é removida
    /// (nunca DROP automático — seção 23). Idempotente se já desabilitado.
    /// </summary>
    public void Desabilitar(DateTimeOffset agoraUtc)
    {
        if (Estado == EstadoProvisionamento.Desabilitado)
        {
            return;
        }

        Estado = EstadoProvisionamento.Desabilitado;
        Tocar(agoraUtc);
    }

    /// <summary>
    /// Atualiza os dados cadastrais EDITÁVEIS: a razão social (espaços nas pontas são removidos; obrigatória e com
    /// limite). O CNPJ e o nome do tenant NÃO mudam: identificam o contribuinte e definem a base física. Acrescente aqui os
    /// campos cadastrais do seu domínio. Devolve se algo mudou (sem mudança, nada é tocado — idempotente).
    /// </summary>
    public bool AtualizarCadastro(string? razaoSocial, DateTimeOffset agoraUtc)
    {
        var razao = Exigir.TextoObrigatorioComLimite(razaoSocial?.Trim(), LimiteRazaoSocial, nameof(RazaoSocial));
        if (string.Equals(razao, RazaoSocial, StringComparison.Ordinal))
        {
            return false;
        }

        RazaoSocial = razao;
        Tocar(agoraUtc);
        return true;
    }

    public void AtualizarRazaoSocial(string novaRazaoSocial, DateTimeOffset agoraUtc)
    {
        RazaoSocial = Exigir.TextoObrigatorioComLimite(novaRazaoSocial, LimiteRazaoSocial, nameof(RazaoSocial));
        Tocar(agoraUtc);
    }

    private static string Truncar(string valor, int limite)
        => valor.Length <= limite ? valor : valor[..limite];

    private void Tocar(DateTimeOffset agoraUtc)
    {
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }
}
