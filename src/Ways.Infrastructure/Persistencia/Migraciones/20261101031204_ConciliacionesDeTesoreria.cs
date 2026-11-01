using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ConciliacionesDeTesoreria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conciliaciones_tesoreria",
                columns: table => new
                {
                    id_conciliacion_tesoreria = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    hasta = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    id_usuario = table.Column<int>(type: "integer", nullable: false),
                    creado_el = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    nota = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conciliaciones_tesoreria", x => x.id_conciliacion_tesoreria);
                    table.ForeignKey(
                        name: "fk_conciliaciones_tesoreria_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conciliaciones_tesoreria_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conciliaciones_tesoreria_usuario",
                        column: x => x.id_usuario,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conciliaciones_tesoreria_empresa_hasta",
                table: "conciliaciones_tesoreria",
                columns: new[] { "id_empresa", "id_tenant", "hasta" });

            migrationBuilder.CreateIndex(
                name: "ix_conciliaciones_tesoreria_tenant",
                table: "conciliaciones_tesoreria",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_conciliaciones_tesoreria_usuario",
                table: "conciliaciones_tesoreria",
                column: "id_usuario");

            migrationBuilder.HabilitarRlsDeTenant("conciliaciones_tesoreria");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conciliaciones_tesoreria");
        }
    }
}
