using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Fiscal;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class FacturacionPorMedioDePagoEsquema : Migration
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
                .Annotation("Npgsql:Enum:estado_facturacion_venta", "pendiente,emitida,rechazada,cancelada")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .Annotation("Npgsql:Enum:motivo_facturacion", "no_requerida,forzada,medio_exige_factura,cupo_alcanzado")
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
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.AddColumn<bool>(
                name: "factura_forzada",
                table: "comprobantes_venta",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "id_venta_origen",
                table: "comprobantes_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "acumulados_de_facturacion",
                columns: table => new
                {
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_medio_pago = table.Column<int>(type: "integer", nullable: false),
                    periodo = table.Column<DateOnly>(type: "date", nullable: false),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    saldo_a_facturar = table.Column<decimal>(type: "numeric(18,6)", nullable: false, defaultValue: 0m),
                    total_vendido = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    total_facturado = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acumulados_de_facturacion", x => new { x.id_empresa, x.id_medio_pago, x.periodo });
                    table.CheckConstraint("ck_acumulados_de_facturacion_periodo_dia_uno", "EXTRACT(DAY FROM periodo) = 1");
                    table.ForeignKey(
                        name: "fk_acumulados_de_facturacion_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_acumulados_de_facturacion_medio_pago",
                        columns: x => new { x.id_medio_pago, x.id_tenant },
                        principalTable: "medios_pago",
                        principalColumns: new[] { "id_medio_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_acumulados_de_facturacion_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "facturaciones_de_venta",
                columns: table => new
                {
                    id_facturacion_venta = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: false),
                    motivo = table.Column<MotivoDeFacturacion>(type: "motivo_facturacion", nullable: false),
                    estado = table.Column<EstadoFacturacionDeVenta>(type: "estado_facturacion_venta", nullable: false),
                    aportes = table.Column<string>(type: "jsonb", nullable: false),
                    periodo = table.Column<DateOnly>(type: "date", nullable: false),
                    id_comprobante_fiscal = table.Column<int>(type: "integer", nullable: true),
                    intentos = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ultimo_error = table.Column<string>(type: "text", nullable: true),
                    proximo_intento_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_facturaciones_de_venta", x => x.id_facturacion_venta);
                    table.CheckConstraint("ck_facturaciones_de_venta_aportes_arreglo", "jsonb_typeof(aportes) = 'array'");
                    table.CheckConstraint("ck_facturaciones_de_venta_emitida_con_comprobante", "estado <> 'emitida'::estado_facturacion_venta OR id_comprobante_fiscal IS NOT NULL");
                    table.CheckConstraint("ck_facturaciones_de_venta_intentos_no_negativo", "intentos >= 0");
                    table.CheckConstraint("ck_facturaciones_de_venta_motivo_requerido", "motivo <> 'no_requerida'::motivo_facturacion");
                    table.CheckConstraint("ck_facturaciones_de_venta_periodo_dia_uno", "EXTRACT(DAY FROM periodo) = 1");
                    table.ForeignKey(
                        name: "fk_facturaciones_de_venta_comprobante",
                        columns: x => new { x.id_comprobante_venta, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_facturaciones_de_venta_comprobante_fiscal",
                        columns: x => new { x.id_comprobante_fiscal, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_facturaciones_de_venta_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_facturaciones_de_venta_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_comprobantes_venta_venta_origen",
                table: "comprobantes_venta",
                columns: new[] { "id_venta_origen", "id_tenant" },
                unique: true,
                filter: "id_venta_origen IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_comprobantes_venta_espejo_fiscal",
                table: "comprobantes_venta",
                sql: "id_venta_origen IS NULL OR resultado_fiscal IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_comprobantes_venta_espejo_sin_turno",
                table: "comprobantes_venta",
                sql: "id_venta_origen IS NULL OR id_turno_caja IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_acumulados_de_facturacion_empresa",
                table: "acumulados_de_facturacion",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_acumulados_de_facturacion_medio_pago",
                table: "acumulados_de_facturacion",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_acumulados_de_facturacion_tenant",
                table: "acumulados_de_facturacion",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_facturaciones_de_venta_empresa",
                table: "facturaciones_de_venta",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_facturaciones_de_venta_pendientes",
                table: "facturaciones_de_venta",
                columns: new[] { "id_tenant", "proximo_intento_en" },
                filter: "estado = 'pendiente'::estado_facturacion_venta");

            migrationBuilder.CreateIndex(
                name: "ix_facturaciones_de_venta_tenant",
                table: "facturaciones_de_venta",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_facturaciones_de_venta_comprobante",
                table: "facturaciones_de_venta",
                columns: new[] { "id_comprobante_venta", "id_tenant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_facturaciones_de_venta_comprobante_fiscal",
                table: "facturaciones_de_venta",
                columns: new[] { "id_comprobante_fiscal", "id_tenant" },
                unique: true,
                filter: "id_comprobante_fiscal IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_comprobantes_venta_venta_origen",
                table: "comprobantes_venta",
                columns: new[] { "id_venta_origen", "id_tenant" },
                principalTable: "comprobantes_venta",
                principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // Solo DDL: ninguna de las dos tablas tiene filas todavía, así que no hay backfill que necesite
            // el GUC de plataforma (rls-migration-backfills no aplica). RLS en la misma migración que las
            // crea (ADR-15).
            migrationBuilder.HabilitarRlsDeTenant("facturaciones_de_venta");
            migrationBuilder.HabilitarRlsDeTenant("acumulados_de_facturacion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_comprobantes_venta_venta_origen",
                table: "comprobantes_venta");

            migrationBuilder.DropTable(
                name: "acumulados_de_facturacion");

            migrationBuilder.DropTable(
                name: "facturaciones_de_venta");

            migrationBuilder.DropIndex(
                name: "ux_comprobantes_venta_venta_origen",
                table: "comprobantes_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comprobantes_venta_espejo_fiscal",
                table: "comprobantes_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comprobantes_venta_espejo_sin_turno",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "factura_forzada",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "id_venta_origen",
                table: "comprobantes_venta");

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
                .OldAnnotation("Npgsql:Enum:estado_facturacion_venta", "pendiente,emitida,rechazada,cancelada")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .OldAnnotation("Npgsql:Enum:motivo_facturacion", "no_requerida,forzada,medio_exige_factura,cupo_alcanzado")
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
        }
    }
}
