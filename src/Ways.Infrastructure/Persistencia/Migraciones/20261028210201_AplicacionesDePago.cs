using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Pagos;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AplicacionesDePago : Migration
    {
        /// <summary>Backfill de datos de <c>Up()</c>, expuesto para probarlo bajo <c>ways_app</c> (rls-migration-backfills).
        /// <c>aplicaciones_pago</c>, <c>pagos</c>, <c>comprobantes_compra</c> y <c>puntos_venta</c> corren bajo FORCE ROW
        /// LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS: sin <c>SET LOCAL app.acceso = 'plataforma'</c> en el
        /// mismo bloque el INSERT ... SELECT no ve ninguna fila y reporta éxito.
        ///
        /// Reconstruye las aplicaciones a partir de <c>pagos.id_comprobante_compra</c>: pagos vivos de categoría proveedor
        /// ligados a una compra confirmada, del mismo proveedor y de la misma empresa (lo que exigen los triggers). Por compra,
        /// en orden (fecha, id_pago), cada pago aplica <c>greatest(0, least(importe, total - suma de los importes
        /// anteriores))</c>; lo que excede el total de la compra y los pagos de compras anuladas quedan sin aplicar (crédito).
        /// Idempotente: no toca un par pago-compra que ya tiene una aplicación viva.</summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y el INSERT reporta éxito.

            INSERT INTO aplicaciones_pago
                (id_tenant, id_pago, id_comprobante_compra, importe, origen, id_empleado, fecha, created_at, updated_at)
            SELECT t.id_tenant, t.id_pago, t.id_comprobante_compra, t.aplicado, 'migracion'::origen_aplicacion_pago,
                   t.id_empleado, t.fecha, t.created_at, t.updated_at
            FROM (
                SELECT p.id_tenant, p.id_pago, p.id_comprobante_compra, p.id_empleado, p.fecha, p.created_at, p.updated_at,
                       GREATEST(0, LEAST(p.importe, c.total - COALESCE(
                           SUM(p.importe) OVER (
                               PARTITION BY p.id_tenant, p.id_comprobante_compra
                               ORDER BY p.fecha, p.id_pago
                               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0))) AS aplicado
                FROM pagos p
                JOIN comprobantes_compra c
                  ON c.id_comprobante_compra = p.id_comprobante_compra AND c.id_tenant = p.id_tenant
                JOIN puntos_venta pv
                  ON pv.id_punto_venta = c.id_punto_venta AND pv.id_tenant = c.id_tenant
                WHERE p.deleted_at IS NULL
                  AND p.categoria = 'proveedor'
                  AND c.estado = 'confirmada'
                  AND p.id_proveedor = c.id_proveedor
                  AND p.id_empresa = pv.id_empresa
            ) t
            WHERE t.aplicado > 0
              AND NOT EXISTS (
                  SELECT 1 FROM aplicaciones_pago a
                  WHERE a.id_pago = t.id_pago AND a.id_comprobante_compra = t.id_comprobante_compra
                    AND a.id_tenant = t.id_tenant AND a.deleted_at IS NULL);
            """;

        /// <summary>Invariantes de las aplicaciones, como constraint triggers DIFERIDOS (los pagos, las compras y las
        /// aplicaciones pueden escribirse en cualquier orden dentro de la misma transacción). Todos fallan con 23514 y
        /// <c>CONSTRAINT</c> igual al nombre del trigger de la aplicación, que es lo que traduce <c>ManejadorDeErrores</c>.
        /// <list type="bullet">
        /// <item><c>tg_aplicaciones_pago_no_excede_pago</c>: la suma de las aplicaciones vivas de un pago no supera su importe,
        /// y un pago dado de baja no tiene aplicaciones vivas. Dispara en las tres operaciones sobre <c>aplicaciones_pago</c>
        /// y en el UPDATE de <c>importe</c>/<c>deleted_at</c> de <c>pagos</c>.</item>
        /// <item><c>tg_aplicaciones_pago_no_excede_compra</c>: la suma de las aplicaciones vivas de una compra no supera su
        /// total, la compra está confirmada y cada pago aplicado es de categoría proveedor, del proveedor y de la empresa
        /// de la compra. Dispara sobre <c>aplicaciones_pago</c>, el UPDATE de <c>total</c>/<c>estado</c> de
        /// <c>comprobantes_compra</c> y el de <c>id_proveedor</c>/<c>categoria</c>/<c>id_empresa</c> de <c>pagos</c>.</item>
        /// </list>
        /// Se crean DESPUÉS del backfill.</summary>
        public const string SqlDeLosTriggers =
            """
            CREATE FUNCTION fn_aplicaciones_pago_exigir_pago(p_id_pago integer, p_id_tenant integer) RETURNS void
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_importe numeric;
                v_baja timestamptz;
                v_suma numeric;
                v_cantidad integer;
            BEGIN
                SELECT importe, deleted_at INTO v_importe, v_baja
                FROM pagos WHERE id_pago = p_id_pago AND id_tenant = p_id_tenant;
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT count(*), COALESCE(sum(importe), 0) INTO v_cantidad, v_suma
                FROM aplicaciones_pago
                WHERE id_pago = p_id_pago AND id_tenant = p_id_tenant AND deleted_at IS NULL;

                IF v_cantidad > 0 AND v_baja IS NOT NULL THEN
                    RAISE EXCEPTION 'El pago % esta dado de baja y tiene aplicaciones vivas.', p_id_pago
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_pago_no_excede_pago';
                END IF;

                IF v_suma > v_importe THEN
                    RAISE EXCEPTION 'Las aplicaciones del pago % (%) superan su importe (%).', p_id_pago, v_suma, v_importe
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_pago_no_excede_pago';
                END IF;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_pago_exigir_compra(p_id_compra integer, p_id_tenant integer) RETURNS void
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_total numeric;
                v_estado text;
                v_id_proveedor integer;
                v_id_empresa integer;
                v_suma numeric;
                v_cantidad integer;
            BEGIN
                SELECT c.total, c.estado::text, c.id_proveedor, pv.id_empresa
                INTO v_total, v_estado, v_id_proveedor, v_id_empresa
                FROM comprobantes_compra c
                JOIN puntos_venta pv ON pv.id_punto_venta = c.id_punto_venta AND pv.id_tenant = c.id_tenant
                WHERE c.id_comprobante_compra = p_id_compra AND c.id_tenant = p_id_tenant;
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT count(*), COALESCE(sum(importe), 0) INTO v_cantidad, v_suma
                FROM aplicaciones_pago
                WHERE id_comprobante_compra = p_id_compra AND id_tenant = p_id_tenant AND deleted_at IS NULL;

                IF v_cantidad = 0 THEN
                    RETURN;
                END IF;

                IF v_estado <> 'confirmada' THEN
                    RAISE EXCEPTION 'La compra % esta en estado % y no admite aplicaciones vivas.', p_id_compra, v_estado
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_pago_no_excede_compra';
                END IF;

                IF v_suma > v_total THEN
                    RAISE EXCEPTION 'Las aplicaciones de la compra % (%) superan su total (%).', p_id_compra, v_suma, v_total
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_pago_no_excede_compra';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM aplicaciones_pago a
                    JOIN pagos p ON p.id_pago = a.id_pago AND p.id_tenant = a.id_tenant
                    WHERE a.id_comprobante_compra = p_id_compra AND a.id_tenant = p_id_tenant AND a.deleted_at IS NULL
                      AND (p.categoria <> 'proveedor'
                           OR p.id_proveedor IS DISTINCT FROM v_id_proveedor
                           OR p.id_empresa <> v_id_empresa)) THEN
                    RAISE EXCEPTION 'La compra % tiene aplicaciones de pagos que no son de proveedor, de su proveedor o de su empresa.',
                        p_id_compra
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_pago_no_excede_compra';
                END IF;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_pago_validar_desde_aplicacion() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP <> 'DELETE' THEN
                    PERFORM fn_aplicaciones_pago_exigir_pago(NEW.id_pago, NEW.id_tenant);
                END IF;
                IF TG_OP = 'DELETE'
                   OR (TG_OP = 'UPDATE' AND (OLD.id_pago, OLD.id_tenant) IS DISTINCT FROM (NEW.id_pago, NEW.id_tenant)) THEN
                    PERFORM fn_aplicaciones_pago_exigir_pago(OLD.id_pago, OLD.id_tenant);
                END IF;
                RETURN NULL;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_pago_validar_compra_desde_aplicacion() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP <> 'DELETE' THEN
                    PERFORM fn_aplicaciones_pago_exigir_compra(NEW.id_comprobante_compra, NEW.id_tenant);
                END IF;
                IF TG_OP = 'DELETE'
                   OR (TG_OP = 'UPDATE'
                       AND (OLD.id_comprobante_compra, OLD.id_tenant) IS DISTINCT FROM (NEW.id_comprobante_compra, NEW.id_tenant)) THEN
                    PERFORM fn_aplicaciones_pago_exigir_compra(OLD.id_comprobante_compra, OLD.id_tenant);
                END IF;
                RETURN NULL;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_pago_validar_pago_desde_pago() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                PERFORM fn_aplicaciones_pago_exigir_pago(NEW.id_pago, NEW.id_tenant);
                RETURN NULL;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_pago_validar_compras_desde_pago() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_id_compra integer;
            BEGIN
                FOR v_id_compra IN
                    SELECT DISTINCT id_comprobante_compra FROM aplicaciones_pago
                    WHERE id_pago = NEW.id_pago AND id_tenant = NEW.id_tenant AND deleted_at IS NULL
                LOOP
                    PERFORM fn_aplicaciones_pago_exigir_compra(v_id_compra, NEW.id_tenant);
                END LOOP;
                RETURN NULL;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_pago_validar_compra_desde_compra() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                PERFORM fn_aplicaciones_pago_exigir_compra(NEW.id_comprobante_compra, NEW.id_tenant);
                RETURN NULL;
            END
            $fn$;

            CREATE CONSTRAINT TRIGGER tg_aplicaciones_pago_no_excede_pago
                AFTER INSERT OR UPDATE OR DELETE ON aplicaciones_pago
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION fn_aplicaciones_pago_validar_desde_aplicacion();

            CREATE CONSTRAINT TRIGGER tg_pagos_cubren_aplicaciones
                AFTER UPDATE OF importe, deleted_at ON pagos
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD.importe IS DISTINCT FROM NEW.importe OR OLD.deleted_at IS DISTINCT FROM NEW.deleted_at)
                EXECUTE FUNCTION fn_aplicaciones_pago_validar_pago_desde_pago();

            CREATE CONSTRAINT TRIGGER tg_aplicaciones_pago_no_excede_compra
                AFTER INSERT OR UPDATE OR DELETE ON aplicaciones_pago
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION fn_aplicaciones_pago_validar_compra_desde_aplicacion();

            CREATE CONSTRAINT TRIGGER tg_comprobantes_compra_admiten_aplicaciones
                AFTER UPDATE OF total, estado ON comprobantes_compra
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD.total IS DISTINCT FROM NEW.total OR OLD.estado IS DISTINCT FROM NEW.estado)
                EXECUTE FUNCTION fn_aplicaciones_pago_validar_compra_desde_compra();

            CREATE CONSTRAINT TRIGGER tg_pagos_coherentes_con_aplicaciones
                AFTER UPDATE OF id_proveedor, categoria, id_empresa ON pagos
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD.id_proveedor IS DISTINCT FROM NEW.id_proveedor
                      OR OLD.categoria IS DISTINCT FROM NEW.categoria
                      OR OLD.id_empresa IS DISTINCT FROM NEW.id_empresa)
                EXECUTE FUNCTION fn_aplicaciones_pago_validar_compras_desde_pago();
            """;

        public const string SqlDeBajaDeLosTriggers =
            """
            DROP TRIGGER IF EXISTS tg_pagos_coherentes_con_aplicaciones ON pagos;
            DROP TRIGGER IF EXISTS tg_comprobantes_compra_admiten_aplicaciones ON comprobantes_compra;
            DROP TRIGGER IF EXISTS tg_pagos_cubren_aplicaciones ON pagos;
            DROP TRIGGER IF EXISTS tg_aplicaciones_pago_no_excede_compra ON aplicaciones_pago;
            DROP TRIGGER IF EXISTS tg_aplicaciones_pago_no_excede_pago ON aplicaciones_pago;
            DROP FUNCTION IF EXISTS fn_aplicaciones_pago_validar_compra_desde_compra();
            DROP FUNCTION IF EXISTS fn_aplicaciones_pago_validar_compras_desde_pago();
            DROP FUNCTION IF EXISTS fn_aplicaciones_pago_validar_pago_desde_pago();
            DROP FUNCTION IF EXISTS fn_aplicaciones_pago_validar_compra_desde_aplicacion();
            DROP FUNCTION IF EXISTS fn_aplicaciones_pago_validar_desde_aplicacion();
            DROP FUNCTION IF EXISTS fn_aplicaciones_pago_exigir_compra(integer, integer);
            DROP FUNCTION IF EXISTS fn_aplicaciones_pago_exigir_pago(integer, integer);
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
                .Annotation("Npgsql:Enum:origen_aplicacion_pago", "aplicacion,migracion,pago_de_compra")
                .Annotation("Npgsql:Enum:origen_compra", "asistente,manual")
                .Annotation("Npgsql:Enum:origen_fondos_pago", "caja_turno,tesoreria")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
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
                .OldAnnotation("Npgsql:Enum:origen_compra", "asistente,manual")
                .OldAnnotation("Npgsql:Enum:origen_fondos_pago", "caja_turno,tesoreria")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,pago,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "aplicaciones_pago",
                columns: table => new
                {
                    id_aplicacion_pago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_pago = table.Column<int>(type: "integer", nullable: false),
                    id_comprobante_compra = table.Column<int>(type: "integer", nullable: false),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    origen = table.Column<OrigenAplicacionPago>(type: "origen_aplicacion_pago", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aplicaciones_pago", x => x.id_aplicacion_pago);
                    table.CheckConstraint("ck_aplicaciones_pago_importe_positivo", "importe > 0");
                    table.ForeignKey(
                        name: "fk_aplicaciones_pago_compra",
                        columns: x => new { x.id_comprobante_compra, x.id_tenant },
                        principalTable: "comprobantes_compra",
                        principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aplicaciones_pago_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aplicaciones_pago_pago",
                        columns: x => new { x.id_pago, x.id_tenant },
                        principalTable: "pagos",
                        principalColumns: new[] { "id_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aplicaciones_pago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_pago_compra",
                table: "aplicaciones_pago",
                columns: new[] { "id_comprobante_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_pago_empleado",
                table: "aplicaciones_pago",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_pago_pago",
                table: "aplicaciones_pago",
                columns: new[] { "id_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_pago_tenant",
                table: "aplicaciones_pago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_aplicaciones_pago_pago_compra",
                table: "aplicaciones_pago",
                columns: new[] { "id_pago", "id_comprobante_compra", "id_tenant" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.HabilitarRlsDeTenant("aplicaciones_pago");

            migrationBuilder.Sql(SqlDelBackfill);
            migrationBuilder.Sql(SqlDeLosTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDeBajaDeLosTriggers);

            migrationBuilder.DropTable(
                name: "aplicaciones_pago");

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
                .Annotation("Npgsql:Enum:origen_compra", "asistente,manual")
                .Annotation("Npgsql:Enum:origen_fondos_pago", "caja_turno,tesoreria")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,descuento_personal,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
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
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cuenta", "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_personal", "ajuste,horas,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,pago,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "importe,peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
