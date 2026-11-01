using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CobroMercadoPagoSinVenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fecha_apartado",
                table: "cobros_mercadopago",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago",
                sql: "estado IN ('creando', 'pendiente', 'aprobado', 'cancelado', 'expirado', 'devuelto', 'sin_venta')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_sin_venta",
                table: "cobros_mercadopago",
                sql: "estado <> 'sin_venta' OR (fecha_apartado IS NOT NULL AND id_comprobante_venta IS NULL AND id_pago_mercadopago IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_sin_venta",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "fecha_apartado",
                table: "cobros_mercadopago");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago",
                sql: "estado IN ('creando', 'pendiente', 'aprobado', 'cancelado', 'expirado', 'devuelto')");
        }
    }
}
