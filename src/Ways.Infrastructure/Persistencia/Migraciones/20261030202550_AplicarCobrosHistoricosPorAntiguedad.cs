using Microsoft.EntityFrameworkCore.Migrations;
using Ways.Domain.Ventas;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AplicarCobrosHistoricosPorAntiguedad : Migration
    {
        /// <summary>
        /// Aplica los recibos históricos a las ventas más viejas del mismo cliente (E7-S4, decisión del dueño 2026-10-30).
        /// Por tenant y cliente, los recibos <c>RC</c> emitidos (sin espejos fiscales) ordenados por (fecha, id) con su
        /// disponible (neto cobrado de los cobros vivos menos lo ya aplicado) y las ventas en cuenta corriente (signo
        /// positivo, no <c>RC</c>, emitidas, sin espejos fiscales) ordenadas igual con su saldo (parte en cuenta corriente
        /// menos lo ya aplicado) se ponen en fila como dos tramos acumulados; cada par recibo-venta recibe el largo de la
        /// superposición de sus tramos. Es el reparto «el recibo más viejo con disponible paga la venta más vieja con saldo»
        /// hecho de una vez, hasta que se acaba uno de los dos lados del cliente. Las aplicaciones manuales que ya existan
        /// descuentan de los dos lados. Un recibo que ya tiene una aplicación manual viva queda fuera del reparto por completo
        /// (alguien ya lo manejó, por ejemplo si S3 se desplegó antes que S4): conserva su saldo como crédito, y por eso
        /// ningún par generado puede colisionar con una aplicación viva.
        /// Valores: <c>origen = 'historial'</c>; <c>fecha</c> = la más tardía de las fechas del recibo y de la venta (el
        /// primer momento en que existían los dos); <c>id_empleado</c> = quien emitió el recibo; <c>created_at</c> y
        /// <c>updated_at</c> = el momento de la migración. Sin filas de auditoría: es una migración.
        /// </summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: aplicaciones_cobro, comprobantes_venta, cobros y medios_pago corren con FORCE ROW
            -- LEVEL SECURITY; sin el GUC de plataforma el rol de la aplicación no ve ninguna fila y el INSERT no escribe nada.
            -- Las invariantes de AplicacionesDeCobro se validan al final de esta sentencia y no al comitear: así no quedan
            -- eventos de trigger pendientes para un ALTER TABLE posterior en la misma transacción.
            SET CONSTRAINTS tg_aplicaciones_cobro_no_excede_cobro, tg_aplicaciones_cobro_no_excede_venta IMMEDIATE;
            WITH aplicado_por_recibo AS (
                SELECT id_tenant, id_comprobante_cobro AS id_comprobante, sum(importe) AS aplicado
                FROM aplicaciones_cobro
                WHERE deleted_at IS NULL
                GROUP BY id_tenant, id_comprobante_cobro
            ),
            aplicado_por_venta AS (
                SELECT id_tenant, id_comprobante_venta AS id_comprobante, sum(importe) AS aplicado
                FROM aplicaciones_cobro
                WHERE deleted_at IS NULL
                GROUP BY id_tenant, id_comprobante_venta
            ),
            neto_por_recibo AS (
                SELECT id_tenant, id_comprobante_venta AS id_comprobante, sum(importe - vuelto) AS neto
                FROM cobros
                WHERE deleted_at IS NULL
                GROUP BY id_tenant, id_comprobante_venta
            ),
            parte_cc_por_venta AS (
                SELECT co.id_tenant, co.id_comprobante_venta AS id_comprobante, sum(co.importe) AS parte_cc
                FROM cobros co
                JOIN medios_pago m ON m.id_medio_pago = co.id_medio_pago AND m.id_tenant = co.id_tenant
                WHERE co.deleted_at IS NULL AND m.comportamiento = 'cuenta_corriente'
                GROUP BY co.id_tenant, co.id_comprobante_venta
            ),
            recibos AS (
                SELECT c.id_tenant, c.id_cliente, c.id_comprobante_venta AS id_recibo, c.fecha, c.id_empleado,
                       n.neto - COALESCE(a.aplicado, 0) AS disponible
                FROM comprobantes_venta c
                JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante
                JOIN neto_por_recibo n ON n.id_tenant = c.id_tenant AND n.id_comprobante = c.id_comprobante_venta
                LEFT JOIN aplicado_por_recibo a ON a.id_tenant = c.id_tenant AND a.id_comprobante = c.id_comprobante_venta
                WHERE t.codigo = 'RC' AND c.estado = 'emitido' AND c.id_venta_origen IS NULL AND c.id_cliente IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM aplicaciones_cobro m
                      WHERE m.id_tenant = c.id_tenant AND m.id_comprobante_cobro = c.id_comprobante_venta
                        AND m.deleted_at IS NULL AND m.origen = 'manual')
            ),
            ventas AS (
                SELECT c.id_tenant, c.id_cliente, c.id_comprobante_venta AS id_venta, c.fecha,
                       p.parte_cc - COALESCE(a.aplicado, 0) AS pendiente
                FROM comprobantes_venta c
                JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante
                JOIN parte_cc_por_venta p ON p.id_tenant = c.id_tenant AND p.id_comprobante = c.id_comprobante_venta
                LEFT JOIN aplicado_por_venta a ON a.id_tenant = c.id_tenant AND a.id_comprobante = c.id_comprobante_venta
                WHERE t.codigo <> 'RC' AND t.signo > 0 AND c.estado = 'emitido' AND c.id_venta_origen IS NULL
                  AND c.id_cliente IS NOT NULL
            ),
            tramos_de_recibos AS (
                SELECT r.*, sum(r.disponible) OVER (
                           PARTITION BY r.id_tenant, r.id_cliente ORDER BY r.fecha, r.id_recibo
                           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS hasta
                FROM recibos r
                WHERE r.disponible > 0
            ),
            tramos_de_ventas AS (
                SELECT v.*, sum(v.pendiente) OVER (
                           PARTITION BY v.id_tenant, v.id_cliente ORDER BY v.fecha, v.id_venta
                           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS hasta
                FROM ventas v
                WHERE v.pendiente > 0
            ),
            pares AS (
                SELECT r.id_tenant, r.id_recibo, v.id_venta, r.id_empleado, GREATEST(r.fecha, v.fecha) AS fecha,
                       LEAST(r.hasta, v.hasta) - GREATEST(r.hasta - r.disponible, v.hasta - v.pendiente) AS importe
                FROM tramos_de_recibos r
                JOIN tramos_de_ventas v ON v.id_tenant = r.id_tenant AND v.id_cliente = r.id_cliente
                 AND v.hasta - v.pendiente < r.hasta AND r.hasta - r.disponible < v.hasta
            )
            INSERT INTO aplicaciones_cobro (
                id_tenant, id_comprobante_cobro, id_comprobante_venta, importe, origen, id_empleado, fecha, created_at,
                updated_at)
            SELECT id_tenant, id_recibo, id_venta, importe, 'historial'::origen_aplicacion_cobro, id_empleado, fecha, now(),
                   now()
            FROM pares
            ORDER BY id_tenant, id_recibo, id_venta;
            SET CONSTRAINTS tg_aplicaciones_cobro_no_excede_cobro, tg_aplicaciones_cobro_no_excede_venta DEFERRED;
            """;

        /// <summary>Retira lo que escribió <see cref="SqlDelBackfill"/> antes de quitar la columna: sin <c>origen</c>, las
        /// aplicaciones históricas no se distinguirían de las manuales.</summary>
        public const string SqlDeBajaDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: mismo motivo que el alta. IMMEDIATE: el DROP COLUMN que sigue no admite eventos de
            -- trigger pendientes sobre la tabla.
            SET CONSTRAINTS tg_aplicaciones_cobro_no_excede_cobro, tg_aplicaciones_cobro_no_excede_venta IMMEDIATE;
            DELETE FROM aplicaciones_cobro WHERE origen = 'historial';
            SET CONSTRAINTS tg_aplicaciones_cobro_no_excede_cobro, tg_aplicaciones_cobro_no_excede_venta DEFERRED;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .Annotation("Npgsql:Enum:categoria_pago", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_facturacion_venta", "cancelada,emitida,pendiente,rechazada")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .Annotation("Npgsql:Enum:motivo_facturacion", "cupo_alcanzado,forzada,medio_exige_factura,no_requerida")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:origen_aplicacion_cobro", "historial,manual")
                .Annotation("Npgsql:Enum:origen_aplicacion_pago", "aplicacion,migracion,pago_de_compra")
                .Annotation("Npgsql:Enum:origen_compra", "asistente,manual")
                .Annotation("Npgsql:Enum:origen_fondos_pago", "caja_turno,tesoreria")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago,redondeo")
                .Annotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .Annotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,pago,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .OldAnnotation("Npgsql:Enum:categoria_pago", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_facturacion_venta", "cancelada,emitida,pendiente,rechazada")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .OldAnnotation("Npgsql:Enum:motivo_facturacion", "cupo_alcanzado,forzada,medio_exige_factura,no_requerida")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:origen_aplicacion_pago", "aplicacion,migracion,pago_de_compra")
                .OldAnnotation("Npgsql:Enum:origen_compra", "asistente,manual")
                .OldAnnotation("Npgsql:Enum:origen_fondos_pago", "caja_turno,tesoreria")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago,redondeo")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,pago,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.AddColumn<OrigenAplicacionCobro>(
                name: "origen",
                table: "aplicaciones_cobro",
                type: "origen_aplicacion_cobro",
                nullable: false,
                defaultValueSql: "'manual'::origen_aplicacion_cobro");

            migrationBuilder.Sql(SqlDelBackfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDeBajaDelBackfill);

            migrationBuilder.DropColumn(
                name: "origen",
                table: "aplicaciones_cobro");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .Annotation("Npgsql:Enum:categoria_pago", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_facturacion_venta", "cancelada,emitida,pendiente,rechazada")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .Annotation("Npgsql:Enum:motivo_facturacion", "cupo_alcanzado,forzada,medio_exige_factura,no_requerida")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:origen_aplicacion_pago", "aplicacion,migracion,pago_de_compra")
                .Annotation("Npgsql:Enum:origen_compra", "asistente,manual")
                .Annotation("Npgsql:Enum:origen_fondos_pago", "caja_turno,tesoreria")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago,redondeo")
                .Annotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .Annotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,pago,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .OldAnnotation("Npgsql:Enum:categoria_pago", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_facturacion_venta", "cancelada,emitida,pendiente,rechazada")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .OldAnnotation("Npgsql:Enum:motivo_facturacion", "cupo_alcanzado,forzada,medio_exige_factura,no_requerida")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:origen_aplicacion_cobro", "historial,manual")
                .OldAnnotation("Npgsql:Enum:origen_aplicacion_pago", "aplicacion,migracion,pago_de_compra")
                .OldAnnotation("Npgsql:Enum:origen_compra", "asistente,manual")
                .OldAnnotation("Npgsql:Enum:origen_fondos_pago", "caja_turno,tesoreria")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago,redondeo")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,pago,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
