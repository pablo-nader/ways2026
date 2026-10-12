using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class Personal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "personal",
                columns: table => new
                {
                    id_personal = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: true),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    documento = table.Column<string>(type: "citext", maxLength: 30, nullable: true),
                    telefono = table.Column<string>(type: "citext", maxLength: 50, nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    valor_hora = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    id_usuario = table.Column<int>(type: "integer", nullable: true),
                    id_cliente = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal", x => x.id_personal);
                    table.UniqueConstraint("ak_personal_id_personal_id_tenant", x => new { x.id_personal, x.id_tenant });
                    table.CheckConstraint("ck_personal_valor_hora", "valor_hora >= 0");
                    table.ForeignKey(
                        name: "fk_personal_cliente",
                        columns: x => new { x.id_cliente, x.id_tenant },
                        principalTable: "clientes",
                        principalColumns: new[] { "id_cliente", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_personal_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_personal_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_personal_usuario",
                        column: x => x.id_usuario,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_personal_cliente",
                table: "personal",
                columns: new[] { "id_cliente", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_personal_empresa",
                table: "personal",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_personal_tenant",
                table: "personal",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_personal_cliente",
                table: "personal",
                column: "id_cliente",
                unique: true,
                filter: "deleted_at IS NULL AND id_cliente IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_personal_usuario",
                table: "personal",
                column: "id_usuario",
                unique: true,
                filter: "deleted_at IS NULL AND id_usuario IS NOT NULL");

            migrationBuilder.HabilitarRlsDeTenant("personal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "personal");
        }
    }
}
