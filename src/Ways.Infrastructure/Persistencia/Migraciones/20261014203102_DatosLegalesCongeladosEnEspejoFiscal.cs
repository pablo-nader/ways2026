using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DatosLegalesCongeladosEnEspejoFiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "datos_legales_fiscales",
                table: "comprobantes_venta",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_comprobantes_venta_datos_legales_espejo",
                table: "comprobantes_venta",
                sql: "datos_legales_fiscales IS NULL OR id_venta_origen IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_comprobantes_venta_datos_legales_espejo",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "datos_legales_fiscales",
                table: "comprobantes_venta");
        }
    }
}
