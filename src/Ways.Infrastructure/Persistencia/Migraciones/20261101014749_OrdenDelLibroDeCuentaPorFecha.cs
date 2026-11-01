using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class OrdenDelLibroDeCuentaPorFecha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_movimientos_cuenta_cuenta_id",
                table: "movimientos_cuenta");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_cuenta_fecha_id",
                table: "movimientos_cuenta",
                columns: new[] { "id_cuenta_tesoreria", "id_tenant", "fecha", "id_movimiento_cuenta" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_movimientos_cuenta_cuenta_fecha_id",
                table: "movimientos_cuenta");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_cuenta_id",
                table: "movimientos_cuenta",
                columns: new[] { "id_cuenta_tesoreria", "id_tenant", "id_movimiento_cuenta" });
        }
    }
}
