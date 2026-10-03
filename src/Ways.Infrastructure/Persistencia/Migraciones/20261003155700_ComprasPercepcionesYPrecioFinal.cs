using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ComprasPercepcionesYPrecioFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "percibe_iibb",
                table: "proveedores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "percibe_iva",
                table: "proveedores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "precios_incluyen_iva",
                table: "proveedores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "alicuota_percepcion_iibb",
                table: "empresas",
                type: "numeric(6,3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "alicuota_percepcion_iva",
                table: "empresas",
                type: "numeric(6,3)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "precios_incluyen_iva",
                table: "comprobantes_compra",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "percepciones_comprobante_compra",
                columns: table => new
                {
                    id_percepcion_comprobante_compra = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_comprobante_compra = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<string>(type: "text", nullable: false),
                    base_imponible = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    alicuota = table.Column<decimal>(type: "numeric(6,3)", nullable: false),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_percepciones_comprobante_compra", x => x.id_percepcion_comprobante_compra);
                    table.CheckConstraint("ck_percepciones_comprobante_compra_alicuota_rango", "alicuota >= 0 AND alicuota <= 100");
                    table.CheckConstraint("ck_percepciones_comprobante_compra_importes_no_negativos", "base_imponible >= 0 AND importe >= 0");
                    table.CheckConstraint("ck_percepciones_comprobante_compra_tipo", "tipo IN ('iibb', 'iva')");
                    table.ForeignKey(
                        name: "fk_percepciones_comprobante_compra_comprobante",
                        columns: x => new { x.id_comprobante_compra, x.id_tenant },
                        principalTable: "comprobantes_compra",
                        principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_percepciones_comprobante_compra_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_empresas_alicuotas_percepcion_rango",
                table: "empresas",
                sql: "(alicuota_percepcion_iibb IS NULL OR (alicuota_percepcion_iibb >= 0 AND alicuota_percepcion_iibb <= 100)) AND (alicuota_percepcion_iva IS NULL OR (alicuota_percepcion_iva >= 0 AND alicuota_percepcion_iva <= 100))");

            migrationBuilder.CreateIndex(
                name: "ix_percepciones_comprobante_compra_comprobante",
                table: "percepciones_comprobante_compra",
                columns: new[] { "id_comprobante_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_percepciones_comprobante_compra_tenant",
                table: "percepciones_comprobante_compra",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_percepciones_comprobante_compra_tipo",
                table: "percepciones_comprobante_compra",
                columns: new[] { "id_comprobante_compra", "tipo" },
                unique: true);

            // Tabla de tenant como alicuotas_comprobante_compra: RLS por id_tenant y FORCE. No hay
            // backfill: las compras existentes quedan sin percepciones y en modo de precios netos
            // por los DEFAULT de las columnas nuevas.
            migrationBuilder.HabilitarRlsDeTenant("percepciones_comprobante_compra");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "percepciones_comprobante_compra");

            migrationBuilder.DropCheckConstraint(
                name: "ck_empresas_alicuotas_percepcion_rango",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "percibe_iibb",
                table: "proveedores");

            migrationBuilder.DropColumn(
                name: "percibe_iva",
                table: "proveedores");

            migrationBuilder.DropColumn(
                name: "precios_incluyen_iva",
                table: "proveedores");

            migrationBuilder.DropColumn(
                name: "alicuota_percepcion_iibb",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "alicuota_percepcion_iva",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "precios_incluyen_iva",
                table: "comprobantes_compra");
        }
    }
}
