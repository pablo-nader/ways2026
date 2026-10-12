using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Caja;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CuentasDeTesoreria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:origen_fondos_gasto", "caja_turno,tesoreria")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .Annotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:origen_fondos_gasto", "caja_turno,tesoreria")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.AddColumn<decimal>(
                name: "comision_porcentaje",
                table: "medios_pago",
                type: "numeric(6,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "id_cuenta_tesoreria",
                table: "medios_pago",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "retencion_ganancias_porcentaje",
                table: "medios_pago",
                type: "numeric(6,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "retencion_iibb_porcentaje",
                table: "medios_pago",
                type: "numeric(6,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "retencion_iva_porcentaje",
                table: "medios_pago",
                type: "numeric(6,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "cuentas_tesoreria",
                columns: table => new
                {
                    id_cuenta_tesoreria = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    saldo_inicial = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cuentas_tesoreria", x => x.id_cuenta_tesoreria);
                    table.UniqueConstraint("ak_cuentas_tesoreria_id_cuenta_tesoreria_id_tenant", x => new { x.id_cuenta_tesoreria, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_cuentas_tesoreria_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cuentas_tesoreria_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movimientos_cuenta",
                columns: table => new
                {
                    id_movimiento_cuenta = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_cuenta_tesoreria = table.Column<int>(type: "integer", nullable: false),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tipo = table.Column<TipoMovimientoCuenta>(type: "tipo_movimiento_cuenta", nullable: false),
                    id_turno_caja = table.Column<int>(type: "integer", nullable: true),
                    id_medio_pago = table.Column<int>(type: "integer", nullable: true),
                    concepto = table.Column<string>(type: "text", nullable: false),
                    inicio = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    ingreso = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    egreso = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    final = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    base_bruta = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    porcentaje_aplicado = table.Column<decimal>(type: "numeric(6,3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movimientos_cuenta", x => x.id_movimiento_cuenta);
                    table.CheckConstraint("ck_movimientos_cuenta_cadena", "final = inicio + ingreso - egreso");
                    table.CheckConstraint("ck_movimientos_cuenta_importes_no_negativos", "ingreso >= 0 AND egreso >= 0");
                    table.CheckConstraint("ck_movimientos_cuenta_un_solo_lado", "ingreso = 0 OR egreso = 0");
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_cuenta_tesoreria",
                        columns: x => new { x.id_cuenta_tesoreria, x.id_tenant },
                        principalTable: "cuentas_tesoreria",
                        principalColumns: new[] { "id_cuenta_tesoreria", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_medio_pago",
                        columns: x => new { x.id_medio_pago, x.id_tenant },
                        principalTable: "medios_pago",
                        principalColumns: new[] { "id_medio_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_turno",
                        columns: x => new { x.id_turno_caja, x.id_tenant },
                        principalTable: "turnos_caja",
                        principalColumns: new[] { "id_turno_caja", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_medios_pago_cuenta_tesoreria",
                table: "medios_pago",
                columns: new[] { "id_cuenta_tesoreria", "id_tenant" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_medios_pago_cuenta_solo_electronico",
                table: "medios_pago",
                sql: "comportamiento = 'electronico' OR (id_cuenta_tesoreria IS NULL AND comision_porcentaje = 0 AND retencion_iibb_porcentaje = 0 AND retencion_iva_porcentaje = 0 AND retencion_ganancias_porcentaje = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_medios_pago_porcentajes_cuenta_rango",
                table: "medios_pago",
                sql: "comision_porcentaje BETWEEN 0 AND 100 AND retencion_iibb_porcentaje BETWEEN 0 AND 100 AND retencion_iva_porcentaje BETWEEN 0 AND 100 AND retencion_ganancias_porcentaje BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_medios_pago_porcentajes_cuenta_suma",
                table: "medios_pago",
                sql: "comision_porcentaje + retencion_iibb_porcentaje + retencion_iva_porcentaje + retencion_ganancias_porcentaje <= 100");

            migrationBuilder.CreateIndex(
                name: "ix_cuentas_tesoreria_empresa",
                table: "cuentas_tesoreria",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_cuentas_tesoreria_tenant",
                table: "cuentas_tesoreria",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_cuentas_tesoreria_nombre_empresa",
                table: "cuentas_tesoreria",
                columns: new[] { "id_tenant", "id_empresa", "nombre" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_cuenta_id",
                table: "movimientos_cuenta",
                columns: new[] { "id_tenant", "id_cuenta_tesoreria", "id_movimiento_cuenta" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_empleado",
                table: "movimientos_cuenta",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "IX_movimientos_cuenta_id_cuenta_tesoreria_id_tenant",
                table: "movimientos_cuenta",
                columns: new[] { "id_cuenta_tesoreria", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_medio_pago",
                table: "movimientos_cuenta",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_tenant",
                table: "movimientos_cuenta",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_turno",
                table: "movimientos_cuenta",
                columns: new[] { "id_turno_caja", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_movimientos_cuenta_cierre",
                table: "movimientos_cuenta",
                columns: new[] { "id_tenant", "id_turno_caja", "id_medio_pago", "tipo" },
                unique: true,
                filter: "id_turno_caja IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_medios_pago_cuenta_tesoreria",
                table: "medios_pago",
                columns: new[] { "id_cuenta_tesoreria", "id_tenant" },
                principalTable: "cuentas_tesoreria",
                principalColumns: new[] { "id_cuenta_tesoreria", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.HabilitarRlsDeTenant("cuentas_tesoreria");
            migrationBuilder.HabilitarRlsDeTenant("movimientos_cuenta");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_medios_pago_cuenta_tesoreria",
                table: "medios_pago");

            migrationBuilder.DropTable(
                name: "movimientos_cuenta");

            migrationBuilder.DropTable(
                name: "cuentas_tesoreria");

            migrationBuilder.DropIndex(
                name: "ix_medios_pago_cuenta_tesoreria",
                table: "medios_pago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_medios_pago_cuenta_solo_electronico",
                table: "medios_pago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_medios_pago_porcentajes_cuenta_rango",
                table: "medios_pago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_medios_pago_porcentajes_cuenta_suma",
                table: "medios_pago");

            migrationBuilder.DropColumn(
                name: "comision_porcentaje",
                table: "medios_pago");

            migrationBuilder.DropColumn(
                name: "id_cuenta_tesoreria",
                table: "medios_pago");

            migrationBuilder.DropColumn(
                name: "retencion_ganancias_porcentaje",
                table: "medios_pago");

            migrationBuilder.DropColumn(
                name: "retencion_iibb_porcentaje",
                table: "medios_pago");

            migrationBuilder.DropColumn(
                name: "retencion_iva_porcentaje",
                table: "medios_pago");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:origen_fondos_gasto", "caja_turno,tesoreria")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:origen_fondos_gasto", "caja_turno,tesoreria")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
