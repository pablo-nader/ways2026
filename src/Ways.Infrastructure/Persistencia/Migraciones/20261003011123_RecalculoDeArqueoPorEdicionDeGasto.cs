using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RecalculoDeArqueoPorEdicionDeGasto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fecha_recalculo",
                table: "turnos_caja",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_empleado_recalculo",
                table: "turnos_caja",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "importe_esperado_original",
                table: "arqueos_turno",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_turnos_caja_empleado_recalculo",
                table: "turnos_caja",
                column: "id_empleado_recalculo");

            migrationBuilder.AddCheckConstraint(
                name: "ck_turnos_caja_recalculo_consistente",
                table: "turnos_caja",
                sql: "(fecha_recalculo IS NULL) = (id_empleado_recalculo IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_turnos_caja_empleado_recalculo",
                table: "turnos_caja",
                column: "id_empleado_recalculo",
                principalTable: "usuarios",
                principalColumn: "id_usuario",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_turnos_caja_empleado_recalculo",
                table: "turnos_caja");

            migrationBuilder.DropIndex(
                name: "ix_turnos_caja_empleado_recalculo",
                table: "turnos_caja");

            migrationBuilder.DropCheckConstraint(
                name: "ck_turnos_caja_recalculo_consistente",
                table: "turnos_caja");

            migrationBuilder.DropColumn(
                name: "fecha_recalculo",
                table: "turnos_caja");

            migrationBuilder.DropColumn(
                name: "id_empleado_recalculo",
                table: "turnos_caja");

            migrationBuilder.DropColumn(
                name: "importe_esperado_original",
                table: "arqueos_turno");
        }
    }
}
