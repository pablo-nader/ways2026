using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RedondeoUnDestino : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_movimientos_cuenta_corriente_proveedor_redondeo_un_destino",
                table: "movimientos_cuenta_corriente_proveedor",
                sql: "tipo <> 'redondeo' OR num_nonnulls(id_comprobante_compra, id_pago) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_movimientos_cuenta_corriente_proveedor_redondeo_un_destino",
                table: "movimientos_cuenta_corriente_proveedor");
        }
    }
}
