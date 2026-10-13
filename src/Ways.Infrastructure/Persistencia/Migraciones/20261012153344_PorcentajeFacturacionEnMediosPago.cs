using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class PorcentajeFacturacionEnMediosPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "porcentaje_facturacion",
                table: "medios_pago",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_medios_pago_porcentaje_facturacion",
                table: "medios_pago",
                sql: "porcentaje_facturacion >= 0 AND porcentaje_facturacion <= 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_medios_pago_porcentaje_facturacion",
                table: "medios_pago");

            migrationBuilder.DropColumn(
                name: "porcentaje_facturacion",
                table: "medios_pago");
        }
    }
}
