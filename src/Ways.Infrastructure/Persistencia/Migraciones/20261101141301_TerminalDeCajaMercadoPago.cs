using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class TerminalDeCajaMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fecha_terminal",
                table: "cajas_mercadopago",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "id_terminal_point",
                table: "cajas_mercadopago",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cajas_mercadopago_terminal",
                table: "cajas_mercadopago",
                sql: "(id_terminal_point IS NULL) = (fecha_terminal IS NULL) AND (id_terminal_point IS NULL OR length(btrim(id_terminal_point)) > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cajas_mercadopago_terminal",
                table: "cajas_mercadopago");

            migrationBuilder.DropColumn(
                name: "fecha_terminal",
                table: "cajas_mercadopago");

            migrationBuilder.DropColumn(
                name: "id_terminal_point",
                table: "cajas_mercadopago");
        }
    }
}
