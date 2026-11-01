using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CajasMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_locales_mercadopago_id_local_id_empresa_id_tenant",
                table: "locales_mercadopago",
                columns: new[] { "id_local_mercadopago", "id_empresa", "id_tenant" });

            migrationBuilder.CreateTable(
                name: "cajas_mercadopago",
                columns: table => new
                {
                    id_caja_mercadopago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_local_mercadopago = table.Column<int>(type: "integer", nullable: false),
                    id_usuario_mercadopago = table.Column<long>(type: "bigint", nullable: false),
                    id_pos_mercadopago = table.Column<long>(type: "bigint", nullable: false),
                    external_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    url_qr = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    fecha_registro = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_sincronizacion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cajas_mercadopago", x => x.id_caja_mercadopago);
                    table.CheckConstraint("ck_cajas_mercadopago_external_id", "length(btrim(external_id)) > 0");
                    table.ForeignKey(
                        name: "fk_cajas_mercadopago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cajas_mercadopago_local",
                        columns: x => new { x.id_local_mercadopago, x.id_empresa, x.id_tenant },
                        principalTable: "locales_mercadopago",
                        principalColumns: new[] { "id_local_mercadopago", "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cajas_mercadopago_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cajas_mercadopago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cajas_mercadopago_empresa",
                table: "cajas_mercadopago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_cajas_mercadopago_local",
                table: "cajas_mercadopago",
                columns: new[] { "id_local_mercadopago", "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_cajas_mercadopago_punto_venta",
                table: "cajas_mercadopago",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_cajas_mercadopago_tenant",
                table: "cajas_mercadopago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_cajas_mercadopago_punto_venta_usuario",
                table: "cajas_mercadopago",
                columns: new[] { "id_punto_venta", "id_usuario_mercadopago" },
                unique: true);

            migrationBuilder.HabilitarRlsDeTenant("cajas_mercadopago");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cajas_mercadopago");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_locales_mercadopago_id_local_id_empresa_id_tenant",
                table: "locales_mercadopago");
        }
    }
}
