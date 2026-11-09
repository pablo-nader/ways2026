using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ComisionInformadaMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "comision_informada",
                table: "cobros_mercadopago",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_comision_informada",
                table: "cobros_mercadopago",
                sql: "comision_informada IS NULL OR comision_informada >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_comision_informada",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "comision_informada",
                table: "cobros_mercadopago");
        }
    }
}
