using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Migrations
{
    /// <inheritdoc />
    public partial class CatalogoInicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "configuracao_email",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Habilitado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Servidor = table.Column<string>(type: "varchar(253)", maxLength: 253, nullable: false),
                    Porta = table.Column<int>(type: "int", nullable: false),
                    Seguranca = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Usuario = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    RemetenteEmail = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false),
                    RemetenteNome = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    UrlPublicaPortal = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    Tema = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false, defaultValue: "Portal"),
                    CorDestaque = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: false, defaultValue: ""),
                    TextoRodape = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false, defaultValue: ""),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuracao_email", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "incidente_seguranca",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Codigo = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    TenantAfetadoId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    Severidade = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Titulo = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    SistemasAfetados = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    CategoriasDados = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    ClassificacaoRisco = table.Column<int>(type: "int", nullable: false),
                    JustificativaClassificacao = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    ResponsavelId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    AbertoPorId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    HashEvidencias = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    ReferenciaEvidencias = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    ResumoEncerramento = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    DetectadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EncerradoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidente_seguranca", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "marca_visual",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Conteudo = table.Column<byte[]>(type: "mediumblob", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    HashSha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    TamanhoBytes = table.Column<long>(type: "bigint", nullable: false),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marca_visual", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "permissao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Chave = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissao", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "registro_auditoria",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Tenant = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    TipoAtor = table.Column<int>(type: "int", nullable: false),
                    acao = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    TipoRecurso = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    RecursoId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    Resultado = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Motivo = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    TraceId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    Origem = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    Job = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    Competencia = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true),
                    Protocolo = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    Categoria = table.Column<int>(type: "int", nullable: false),
                    ValoresAnteriores = table.Column<string>(type: "text", nullable: true),
                    ValoresNovos = table.Column<string>(type: "text", nullable: true),
                    Metadados = table.Column<string>(type: "text", nullable: true),
                    OcorridoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    integridade_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registro_auditoria", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "registro_backup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AlvoLogico = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IniciadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ConcluidoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TamanhoBytes = table.Column<long>(type: "bigint", nullable: false),
                    HashSha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    AlgoritmoHash = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Versao = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    LocalArmazenamentoLogico = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    Criptografado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ChaveRef = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Imutavel = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RetencaoAteUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RestoreTestado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RestoreTestadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UltimoErro = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    CopiaExterna = table.Column<int>(type: "int", nullable: true),
                    CopiaExternaEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registro_backup", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "saude_worker",
                columns: table => new
                {
                    Worker = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Ativo = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IniciadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UltimaBatidaUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UltimoSucessoUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TotalProcessado = table.Column<long>(type: "bigint", nullable: false),
                    UltimoErroUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UltimoErroCodigo = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    TotalErros = table.Column<long>(type: "bigint", nullable: false),
                    Reinicios = table.Column<int>(type: "int", nullable: false),
                    GravadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saude_worker", x => x.Worker);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "tarefa_segundo_plano",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SolicitanteId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Parametros = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    ChaveIdempotencia = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Estado = table.Column<int>(type: "int", nullable: false),
                    Tentativas = table.Column<int>(type: "int", nullable: false),
                    CriadaEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    IniciadaEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConcluidaEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseAteUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Executor = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Resultado = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    Motivo = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tarefa_segundo_plano", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    cnpj = table.Column<string>(type: "varchar(14)", maxLength: 14, nullable: false),
                    nome = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    RazaoSocial = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Estado = table.Column<int>(type: "int", nullable: false),
                    NomeBaseFisica = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ReferenciaSegredoConexao = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    SchemaVersion = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    UltimaMigracao = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    UltimoErro = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "usuario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Login = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Nome = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    hash_senha = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    segredo_mfa_protegido = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true),
                    segredo_mfa_pendente_protegido = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true),
                    mfa_habilitado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    mfa_confirmado_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    acessos_falhos = table.Column<int>(type: "int", nullable: false),
                    bloqueado_ate_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ultimo_acesso_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false),
                    security_stamp = table.Column<Guid>(type: "char(36)", nullable: false),
                    administrador_plataforma = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    mfa_obrigatorio = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    trocar_senha_proximo_acesso = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usuario", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "incidente_timeline",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    IncidenteId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Sequencia = table.Column<int>(type: "int", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    AtorId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    Descricao = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    OcorridoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidente_timeline", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incidente_timeline_incidente_seguranca_IncidenteId",
                        column: x => x.IncidenteId,
                        principalTable: "incidente_seguranca",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "papel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Nome = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<Guid>(type: "char(36)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_papel", x => x.Id);
                    table.ForeignKey(
                        name: "FK_papel_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "codigo_recuperacao_mfa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "char(36)", nullable: false),
                    hash_codigo = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UsadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_codigo_recuperacao_mfa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_codigo_recuperacao_mfa_usuario_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "token_recuperacao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "char(36)", nullable: false),
                    hash_token = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiraEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UsadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_recuperacao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_token_recuperacao_usuario_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "usuario_tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RevogadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usuario_tenant", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usuario_tenant_tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_usuario_tenant_usuario_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "papel_permissao",
                columns: table => new
                {
                    PapelId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PermissaoId = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_papel_permissao", x => new { x.PapelId, x.PermissaoId });
                    table.ForeignKey(
                        name: "FK_papel_permissao_papel_PapelId",
                        column: x => x.PapelId,
                        principalTable: "papel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_papel_permissao_permissao_PermissaoId",
                        column: x => x.PermissaoId,
                        principalTable: "permissao",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.CreateTable(
                name: "usuario_tenant_papel",
                columns: table => new
                {
                    UsuarioTenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PapelId = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usuario_tenant_papel", x => new { x.UsuarioTenantId, x.PapelId });
                    table.ForeignKey(
                        name: "FK_usuario_tenant_papel_papel_PapelId",
                        column: x => x.PapelId,
                        principalTable: "papel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usuario_tenant_papel_usuario_tenant_UsuarioTenantId",
                        column: x => x.UsuarioTenantId,
                        principalTable: "usuario_tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_as_cs");

            migrationBuilder.InsertData(
                table: "papel",
                columns: new[] { "Id", "Descricao", "Nome", "tenant_id" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-000000000001"), "Acesso somente leitura.", "Leitor", null },
                    { new Guid("11111111-1111-1111-1111-000000000002"), "Cadastro, edição e importação de dados.", "Analista", null },
                    { new Guid("11111111-1111-1111-1111-000000000003"), "Administração completa do tenant.", "Administrador", null }
                });

            migrationBuilder.InsertData(
                table: "permissao",
                columns: new[] { "Id", "Chave", "Descricao" },
                values: new object[,]
                {
                    { new Guid("22222222-2222-2222-2222-000000000001"), "dados.visualizar", "VisualizarDados" },
                    { new Guid("22222222-2222-2222-2222-000000000002"), "dados.cadastrar", "Cadastrar" },
                    { new Guid("22222222-2222-2222-2222-000000000003"), "dados.editar", "Editar" },
                    { new Guid("22222222-2222-2222-2222-000000000004"), "dados.excluir", "Excluir" },
                    { new Guid("22222222-2222-2222-2222-000000000005"), "dados.importar", "Importar" },
                    { new Guid("22222222-2222-2222-2222-000000000006"), "admin.tenant", "AdministrarTenant" },
                    { new Guid("22222222-2222-2222-2222-000000000007"), "admin.usuarios", "AdministrarUsuarios" },
                    { new Guid("22222222-2222-2222-2222-000000000008"), "auditoria.visualizar", "VisualizarAuditoria" }
                });

            migrationBuilder.InsertData(
                table: "papel_permissao",
                columns: new[] { "PapelId", "PermissaoId" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-000000000001"), new Guid("22222222-2222-2222-2222-000000000001") },
                    { new Guid("11111111-1111-1111-1111-000000000002"), new Guid("22222222-2222-2222-2222-000000000001") },
                    { new Guid("11111111-1111-1111-1111-000000000002"), new Guid("22222222-2222-2222-2222-000000000002") },
                    { new Guid("11111111-1111-1111-1111-000000000002"), new Guid("22222222-2222-2222-2222-000000000003") },
                    { new Guid("11111111-1111-1111-1111-000000000002"), new Guid("22222222-2222-2222-2222-000000000005") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000001") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000002") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000003") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000004") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000005") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000006") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000007") },
                    { new Guid("11111111-1111-1111-1111-000000000003"), new Guid("22222222-2222-2222-2222-000000000008") }
                });

            migrationBuilder.CreateIndex(
                name: "ux_codigo_mfa_usuario_hash",
                table: "codigo_recuperacao_mfa",
                columns: new[] { "UsuarioId", "hash_codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incidente_status",
                table: "incidente_seguranca",
                columns: new[] { "Status", "DetectadoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_incidente_tenant",
                table: "incidente_seguranca",
                column: "TenantAfetadoId");

            migrationBuilder.CreateIndex(
                name: "ux_incidente_codigo",
                table: "incidente_seguranca",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_incidente_timeline_seq",
                table: "incidente_timeline",
                columns: new[] { "IncidenteId", "Sequencia" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_papel_tenant_nome",
                table: "papel",
                columns: new[] { "tenant_id", "Nome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_papel_permissao_PermissaoId",
                table: "papel_permissao",
                column: "PermissaoId");

            migrationBuilder.CreateIndex(
                name: "ux_permissao_chave",
                table: "permissao",
                column: "Chave",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_acao_data",
                table: "registro_auditoria",
                columns: new[] { "acao", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_ator_data",
                table: "registro_auditoria",
                columns: new[] { "PrincipalId", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_competencia",
                table: "registro_auditoria",
                columns: new[] { "Tenant", "Competencia" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_correlation",
                table: "registro_auditoria",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_protocolo",
                table: "registro_auditoria",
                columns: new[] { "Tenant", "Protocolo" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_recurso",
                table: "registro_auditoria",
                columns: new[] { "TipoRecurso", "RecursoId" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_tenant_data",
                table: "registro_auditoria",
                columns: new[] { "Tenant", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_trace",
                table: "registro_auditoria",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "ix_backup_retencao",
                table: "registro_backup",
                column: "RetencaoAteUtc");

            migrationBuilder.CreateIndex(
                name: "ix_backup_status",
                table: "registro_backup",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ix_backup_tenant_tipo_status",
                table: "registro_backup",
                columns: new[] { "TenantId", "Tipo", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_tipo_iniciado",
                table: "registro_backup",
                columns: new[] { "Tipo", "IniciadoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_tarefa_estado_criada",
                table: "tarefa_segundo_plano",
                columns: new[] { "Estado", "CriadaEmUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_tarefa_tenant_tipo_chave",
                table: "tarefa_segundo_plano",
                columns: new[] { "TenantId", "Tipo", "ChaveIdempotencia" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_estado",
                table: "tenant",
                column: "Estado");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_base_fisica",
                table: "tenant",
                column: "NomeBaseFisica",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_cnpj",
                table: "tenant",
                column: "cnpj",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_nome",
                table: "tenant",
                column: "nome",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_token_recuperacao_UsuarioId",
                table: "token_recuperacao",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "ux_token_recuperacao_hash",
                table: "token_recuperacao",
                column: "hash_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_usuario_email",
                table: "usuario",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_usuario_login",
                table: "usuario",
                column: "Login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usuario_tenant_TenantId",
                table: "usuario_tenant",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_usuario_tenant",
                table: "usuario_tenant",
                columns: new[] { "UsuarioId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usuario_tenant_papel_PapelId",
                table: "usuario_tenant_papel",
                column: "PapelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "codigo_recuperacao_mfa");

            migrationBuilder.DropTable(
                name: "configuracao_email");

            migrationBuilder.DropTable(
                name: "incidente_timeline");

            migrationBuilder.DropTable(
                name: "marca_visual");

            migrationBuilder.DropTable(
                name: "papel_permissao");

            migrationBuilder.DropTable(
                name: "registro_auditoria");

            migrationBuilder.DropTable(
                name: "registro_backup");

            migrationBuilder.DropTable(
                name: "saude_worker");

            migrationBuilder.DropTable(
                name: "tarefa_segundo_plano");

            migrationBuilder.DropTable(
                name: "token_recuperacao");

            migrationBuilder.DropTable(
                name: "usuario_tenant_papel");

            migrationBuilder.DropTable(
                name: "incidente_seguranca");

            migrationBuilder.DropTable(
                name: "permissao");

            migrationBuilder.DropTable(
                name: "papel");

            migrationBuilder.DropTable(
                name: "usuario_tenant");

            migrationBuilder.DropTable(
                name: "tenant");

            migrationBuilder.DropTable(
                name: "usuario");
        }
    }
}
