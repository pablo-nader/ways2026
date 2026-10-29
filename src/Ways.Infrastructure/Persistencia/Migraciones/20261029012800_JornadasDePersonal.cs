using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class JornadasDePersonal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_movimientos_personal_horas",
                table: "movimientos_personal");

            migrationBuilder.CreateTable(
                name: "jornadas_personal",
                columns: table => new
                {
                    id_jornada = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_personal = table.Column<int>(type: "integer", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    horas = table.Column<decimal>(type: "numeric(4,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jornadas_personal", x => x.id_jornada);
                    table.CheckConstraint("ck_jornadas_personal_horas", "horas >= 0 AND horas <= 24");
                    table.ForeignKey(
                        name: "fk_jornadas_personal_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_jornadas_personal_personal",
                        columns: x => new { x.id_personal, x.id_tenant },
                        principalTable: "personal",
                        principalColumns: new[] { "id_personal", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_jornadas_personal_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_movimientos_personal_horas",
                table: "movimientos_personal",
                sql: "tipo <> 'horas' OR (horas IS NOT NULL AND horas >= -24 AND horas <= 24 AND horas <> 0 AND valor_hora IS NOT NULL AND valor_hora >= 0 AND importe = round(horas * valor_hora, 2))");

            migrationBuilder.CreateIndex(
                name: "ix_jornadas_personal_empleado",
                table: "jornadas_personal",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_jornadas_personal_tenant",
                table: "jornadas_personal",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_jornadas_personal_personal_fecha",
                table: "jornadas_personal",
                columns: new[] { "id_personal", "id_tenant", "fecha" },
                unique: true);

            migrationBuilder.HabilitarRlsDeTenant("jornadas_personal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jornadas_personal");

            migrationBuilder.DropCheckConstraint(
                name: "ck_movimientos_personal_horas",
                table: "movimientos_personal");

            migrationBuilder.AddCheckConstraint(
                name: "ck_movimientos_personal_horas",
                table: "movimientos_personal",
                sql: "tipo <> 'horas' OR (horas IS NOT NULL AND horas > 0 AND horas <= 24 AND valor_hora IS NOT NULL AND valor_hora >= 0 AND importe = round(horas * valor_hora, 2))");
        }
    }
}
