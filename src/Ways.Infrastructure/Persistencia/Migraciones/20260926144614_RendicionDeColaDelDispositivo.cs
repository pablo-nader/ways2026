using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RendicionDeColaDelDispositivo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "entregado_hasta",
                table: "reservas_numeracion",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pendientes",
                table: "reservas_numeracion",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reportado_at",
                table: "reservas_numeracion",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservas_numeracion_entregado_en_rango",
                table: "reservas_numeracion",
                sql: "entregado_hasta IS NULL OR (entregado_hasta >= desde - 1 AND entregado_hasta <= hasta)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservas_numeracion_pendientes_no_negativo",
                table: "reservas_numeracion",
                sql: "pendientes IS NULL OR pendientes >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservas_numeracion_reporte_consistente",
                table: "reservas_numeracion",
                sql: "(entregado_hasta IS NULL AND pendientes IS NULL AND reportado_at IS NULL) OR (entregado_hasta IS NOT NULL AND pendientes IS NOT NULL AND reportado_at IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reservas_numeracion_entregado_en_rango",
                table: "reservas_numeracion");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservas_numeracion_pendientes_no_negativo",
                table: "reservas_numeracion");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservas_numeracion_reporte_consistente",
                table: "reservas_numeracion");

            migrationBuilder.DropColumn(
                name: "entregado_hasta",
                table: "reservas_numeracion");

            migrationBuilder.DropColumn(
                name: "pendientes",
                table: "reservas_numeracion");

            migrationBuilder.DropColumn(
                name: "reportado_at",
                table: "reservas_numeracion");
        }
    }
}
