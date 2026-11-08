using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AnulacionDePagoDePersonal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_movimiento_anulado",
                table: "movimientos_personal",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_movimientos_personal_movimiento_anulado",
                table: "movimientos_personal",
                columns: new[] { "id_movimiento_anulado", "id_tenant" },
                unique: true,
                filter: "id_movimiento_anulado IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_movimientos_personal_anulacion",
                table: "movimientos_personal",
                sql: "id_movimiento_anulado IS NULL OR tipo = 'ajuste'");

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_personal_movimiento_anulado",
                table: "movimientos_personal",
                columns: new[] { "id_movimiento_anulado", "id_tenant" },
                principalTable: "movimientos_personal",
                principalColumns: new[] { "id_movimiento", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_personal_movimiento_anulado",
                table: "movimientos_personal");

            migrationBuilder.DropIndex(
                name: "ux_movimientos_personal_movimiento_anulado",
                table: "movimientos_personal");

            migrationBuilder.DropCheckConstraint(
                name: "ck_movimientos_personal_anulacion",
                table: "movimientos_personal");

            migrationBuilder.DropColumn(
                name: "id_movimiento_anulado",
                table: "movimientos_personal");
        }
    }
}
