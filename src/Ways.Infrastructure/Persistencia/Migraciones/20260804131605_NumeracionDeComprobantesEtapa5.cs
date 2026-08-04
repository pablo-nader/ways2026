using Microsoft.EntityFrameworkCore.Migrations;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class NumeracionDeComprobantesEtapa5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "numeraciones_comprobante",
                columns: table => new
                {
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    tipo_comprobante = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    proximo_numero = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_numeraciones_comprobante", x => new { x.id_punto_venta, x.tipo_comprobante });
                    table.ForeignKey(
                        name: "fk_numeraciones_comprobante_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_numeraciones_comprobante_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_numeraciones_comprobante_punto_venta",
                table: "numeraciones_comprobante",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_numeraciones_comprobante_tenant",
                table: "numeraciones_comprobante",
                column: "id_tenant");

            // RLS (ADR-4/ADR-15, DB CHANGE GATE aprobado 2026-08-04): la tabla nueva activa su
            // policy en la misma migración que la crea (design: Migration Sequencing).
            migrationBuilder.HabilitarRlsDeTenant("numeraciones_comprobante");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "numeraciones_comprobante");
        }
    }
}
