using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CobrosMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "proveedor",
                table: "medios_pago",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cobros_mercadopago",
                columns: table => new
                {
                    id_cobro_mercadopago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_dispositivo = table.Column<int>(type: "integer", nullable: false),
                    id_medio_pago = table.Column<int>(type: "integer", nullable: false),
                    numero_preasignado = table.Column<long>(type: "bigint", nullable: false),
                    intento = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    importe = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    external_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_pos_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    clave_idempotencia = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    id_orden_mercadopago = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    id_pago_mercadopago = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    referencia_pago_mercadopago = table.Column<long>(type: "bigint", nullable: true),
                    importe_pagado = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    qr_data = table.Column<string>(type: "text", nullable: true),
                    expira_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_ultima_consulta = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fecha_aprobacion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cobros_mercadopago", x => x.id_cobro_mercadopago);
                    table.CheckConstraint("ck_cobros_mercadopago_aprobacion", "estado <> 'aprobado' OR fecha_aprobacion IS NOT NULL");
                    table.CheckConstraint("ck_cobros_mercadopago_estado", "estado IN ('creando', 'pendiente', 'aprobado', 'cancelado', 'expirado', 'devuelto')");
                    table.CheckConstraint("ck_cobros_mercadopago_importe", "importe > 0");
                    table.CheckConstraint("ck_cobros_mercadopago_importe_pagado", "importe_pagado IS NULL OR importe_pagado >= 0");
                    table.CheckConstraint("ck_cobros_mercadopago_intento", "intento >= 1");
                    table.CheckConstraint("ck_cobros_mercadopago_numero", "numero_preasignado > 0");
                    table.CheckConstraint("ck_cobros_mercadopago_orden", "estado IN ('creando', 'expirado') OR id_orden_mercadopago IS NOT NULL");
                    table.CheckConstraint("ck_cobros_mercadopago_tipo", "tipo IN ('qr', 'point')");
                    table.ForeignKey(
                        name: "fk_cobros_mercadopago_dispositivo",
                        columns: x => new { x.id_dispositivo, x.id_tenant },
                        principalTable: "dispositivos",
                        principalColumns: new[] { "id_dispositivo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cobros_mercadopago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cobros_mercadopago_medio_pago",
                        columns: x => new { x.id_medio_pago, x.id_tenant },
                        principalTable: "medios_pago",
                        principalColumns: new[] { "id_medio_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cobros_mercadopago_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cobros_mercadopago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_medios_pago_proveedor",
                table: "medios_pago",
                sql: "proveedor IS NULL OR proveedor IN ('mercadopago_qr', 'mercadopago_point')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_medios_pago_proveedor_solo_electronico",
                table: "medios_pago",
                sql: "proveedor IS NULL OR comportamiento = 'electronico'");

            migrationBuilder.CreateIndex(
                name: "ix_cobros_mercadopago_dispositivo",
                table: "cobros_mercadopago",
                columns: new[] { "id_dispositivo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_cobros_mercadopago_empresa",
                table: "cobros_mercadopago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "IX_cobros_mercadopago_id_punto_venta_id_tenant",
                table: "cobros_mercadopago",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_cobros_mercadopago_medio_pago",
                table: "cobros_mercadopago",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_cobros_mercadopago_tenant",
                table: "cobros_mercadopago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_cobros_mercadopago_intento",
                table: "cobros_mercadopago",
                columns: new[] { "id_punto_venta", "numero_preasignado", "intento" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_cobros_mercadopago_vivo_numero",
                table: "cobros_mercadopago",
                columns: new[] { "id_punto_venta", "numero_preasignado" },
                unique: true,
                filter: "estado IN ('creando', 'pendiente', 'aprobado')");

            migrationBuilder.HabilitarRlsDeTenant("cobros_mercadopago");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cobros_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_medios_pago_proveedor",
                table: "medios_pago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_medios_pago_proveedor_solo_electronico",
                table: "medios_pago");

            migrationBuilder.DropColumn(
                name: "proveedor",
                table: "medios_pago");
        }
    }
}
