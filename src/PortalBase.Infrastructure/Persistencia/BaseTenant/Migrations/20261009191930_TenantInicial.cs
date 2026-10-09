using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortalBase.Infrastructure.Persistencia.BaseTenant.Migrations
{
    /// <inheritdoc />
    public partial class TenantInicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "arquivo_importacao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    NomeOriginalSanitizado = table.Column<string>(type: "varchar(260)", maxLength: 260, nullable: false),
                    NomeSeguro = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Extensao = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    ContentTypeDetectado = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    TamanhoBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Estado = table.Column<int>(type: "int", nullable: false),
                    ResultadoScan = table.Column<int>(type: "int", nullable: false),
                    EscaneadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ReferenciaArmazenamento = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CategoriaRetencao = table.Column<int>(type: "int", nullable: false),
                    ExpiraEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    MotivoRejeicao = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    EnviadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_arquivo_importacao", x => x.Id);
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

            migrationBuilder.CreateIndex(
                name: "ix_arquivo_estado",
                table: "arquivo_importacao",
                column: "Estado");

            migrationBuilder.CreateIndex(
                name: "ux_arquivo_sha256",
                table: "arquivo_importacao",
                column: "Sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "arquivo_importacao");

            migrationBuilder.DropTable(
                name: "marca_visual");
        }
    }
}
