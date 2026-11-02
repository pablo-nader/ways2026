using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CobroMercadoPagoOrdenUnica : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_cobros_mercadopago_orden",
                table: "cobros_mercadopago",
                column: "id_orden_mercadopago",
                unique: true,
                filter: "id_orden_mercadopago IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_cobros_mercadopago_orden",
                table: "cobros_mercadopago");
        }
    }
}
