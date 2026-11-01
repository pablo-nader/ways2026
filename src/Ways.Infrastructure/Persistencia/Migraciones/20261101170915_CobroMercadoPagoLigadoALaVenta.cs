using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CobroMercadoPagoLigadoALaVenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_comprobante_venta",
                table: "cobros_mercadopago",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_cobro_mercadopago",
                table: "cobros",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_cobros_mercadopago_id_id_tenant",
                table: "cobros_mercadopago",
                columns: new[] { "id_cobro_mercadopago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago",
                columns: new[] { "id_comprobante_venta", "id_tenant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cobros_cobro_mercadopago",
                table: "cobros",
                columns: new[] { "id_cobro_mercadopago", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_cobros_cobro_mercadopago",
                table: "cobros",
                columns: new[] { "id_cobro_mercadopago", "id_tenant" },
                principalTable: "cobros_mercadopago",
                principalColumns: new[] { "id_cobro_mercadopago", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago",
                columns: new[] { "id_comprobante_venta", "id_tenant" },
                principalTable: "comprobantes_venta",
                principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_cobros_cobro_mercadopago",
                table: "cobros");

            migrationBuilder.DropForeignKey(
                name: "fk_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_cobros_mercadopago_id_id_tenant",
                table: "cobros_mercadopago");

            migrationBuilder.DropIndex(
                name: "ux_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago");

            migrationBuilder.DropIndex(
                name: "ix_cobros_cobro_mercadopago",
                table: "cobros");

            migrationBuilder.DropColumn(
                name: "id_comprobante_venta",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "id_cobro_mercadopago",
                table: "cobros");
        }
    }
}
