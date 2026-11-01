using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class LocalesMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "locales_mercadopago",
                columns: table => new
                {
                    id_local_mercadopago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_sucursal = table.Column<int>(type: "integer", nullable: false),
                    id_usuario_mercadopago = table.Column<long>(type: "bigint", nullable: false),
                    id_store_mercadopago = table.Column<long>(type: "bigint", nullable: false),
                    external_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    fecha_registro = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_sincronizacion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_locales_mercadopago", x => x.id_local_mercadopago);
                    table.CheckConstraint("ck_locales_mercadopago_external_id", "length(btrim(external_id)) > 0");
                    table.ForeignKey(
                        name: "fk_locales_mercadopago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_locales_mercadopago_sucursal",
                        columns: x => new { x.id_sucursal, x.id_empresa, x.id_tenant },
                        principalTable: "sucursales",
                        principalColumns: new[] { "id_sucursal", "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_locales_mercadopago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_locales_mercadopago_empresa",
                table: "locales_mercadopago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_locales_mercadopago_sucursal",
                table: "locales_mercadopago",
                columns: new[] { "id_sucursal", "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_locales_mercadopago_tenant",
                table: "locales_mercadopago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_locales_mercadopago_sucursal_usuario",
                table: "locales_mercadopago",
                columns: new[] { "id_sucursal", "id_usuario_mercadopago" },
                unique: true);

            migrationBuilder.HabilitarRlsDeTenant("locales_mercadopago");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "locales_mercadopago");
        }
    }
}
