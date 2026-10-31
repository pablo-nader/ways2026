using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AplicacionesEntreEmpresas : Migration
    {
        /// <summary>Reemplaza <c>fn_aplicaciones_pago_exigir_compra</c> (creada por <c>AplicacionesDePago</c>) sin la
        /// condición de misma empresa: un pago puede aplicarse a una compra de otra empresa del mismo tenant, que es lo que
        /// ya contaba como pagado antes de las aplicaciones. Se conservan los demás invariantes: la compra está confirmada,
        /// lo aplicado no supera su total y cada pago aplicado es de categoría proveedor y del proveedor de la compra.</summary>
        public const string SqlDeLaFuncionSinEmpresa =
            """
            CREATE OR REPLACE FUNCTION fn_aplicaciones_pago_exigir_compra(p_id_compra integer, p_id_tenant integer) RETURNS void
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_total numeric;
                v_estado text;
                v_id_proveedor integer;
                v_suma numeric;
                v_cantidad integer;
            BEGIN
                SELECT c.total, c.estado::text, c.id_proveedor
                INTO v_total, v_estado, v_id_proveedor
                FROM comprobantes_compra c
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
                           OR p.id_proveedor IS DISTINCT FROM v_id_proveedor)) THEN
                    RAISE EXCEPTION 'La compra % tiene aplicaciones de pagos que no son de proveedor o de su proveedor.',
                        p_id_compra
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_pago_no_excede_compra';
                END IF;
            END
            $fn$;
            """;

        /// <summary>Backfill de datos de <c>Up()</c>, expuesto para probarlo bajo <c>ways_app</c> (rls-migration-backfills).
        /// Sin <c>SET LOCAL app.acceso = 'plataforma'</c> en el mismo bloque el INSERT ... SELECT no ve ninguna fila bajo FORCE
        /// ROW LEVEL SECURITY y reporta éxito.
        ///
        /// Completa lo que el backfill de <c>AplicacionesDePago</c> salteó solo por la empresa: pagos vivos de categoría
        /// proveedor ligados a una compra confirmada del mismo proveedor pero de otra empresa. Por compra, en orden
        /// (fecha, id_pago), cada pago aplica <c>greatest(0, least(lo que le queda disponible al pago, lo que le queda a la
        /// compra - suma de los importes de los candidatos anteriores))</c>; lo que le queda a la compra descuenta las
        /// aplicaciones vivas que ya tiene. Idempotente: un par pago-compra con aplicación viva no es candidato.</summary>
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
                       GREATEST(0, LEAST(
                           p.importe - COALESCE((
                               SELECT SUM(ap.importe) FROM aplicaciones_pago ap
                               WHERE ap.id_pago = p.id_pago AND ap.id_tenant = p.id_tenant AND ap.deleted_at IS NULL), 0),
                           c.total
                               - COALESCE((
                                   SELECT SUM(ac.importe) FROM aplicaciones_pago ac
                                   WHERE ac.id_comprobante_compra = c.id_comprobante_compra AND ac.id_tenant = c.id_tenant
                                     AND ac.deleted_at IS NULL), 0)
                               - COALESCE(
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
                  AND p.id_empresa <> pv.id_empresa
                  AND NOT EXISTS (
                      SELECT 1 FROM aplicaciones_pago a
                      WHERE a.id_pago = p.id_pago AND a.id_comprobante_compra = p.id_comprobante_compra
                        AND a.id_tenant = p.id_tenant AND a.deleted_at IS NULL)
            ) t
            WHERE t.aplicado > 0;
            """;

        /// <summary>Retira de <c>Down()</c> las aplicaciones entre empresas (las da de baja, nunca las borra) antes de
        /// restituir la función con la condición de misma empresa: de otro modo el invariante restituido quedaría violado
        /// por filas que ya existen. Mismo <c>SET LOCAL</c> que el backfill.</summary>
        public const string SqlDeLaBajaDeLasAplicacionesEntreEmpresas =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y el UPDATE reporta éxito.

            UPDATE aplicaciones_pago a
            SET deleted_at = now(), updated_at = now()
            FROM pagos p, comprobantes_compra c, puntos_venta pv
            WHERE p.id_pago = a.id_pago AND p.id_tenant = a.id_tenant
              AND c.id_comprobante_compra = a.id_comprobante_compra AND c.id_tenant = a.id_tenant
              AND pv.id_punto_venta = c.id_punto_venta AND pv.id_tenant = c.id_tenant
              AND a.deleted_at IS NULL
              AND p.id_empresa <> pv.id_empresa;
            """;

        /// <summary>La función tal cual la creó <c>AplicacionesDePago</c> (con la condición de misma empresa).</summary>
        public const string SqlDeLaFuncionConEmpresa =
            """
            CREATE OR REPLACE FUNCTION fn_aplicaciones_pago_exigir_compra(p_id_compra integer, p_id_tenant integer) RETURNS void
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
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDeLaFuncionSinEmpresa);
            migrationBuilder.Sql(SqlDelBackfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDeLaBajaDeLasAplicacionesEntreEmpresas);
            migrationBuilder.Sql(SqlDeLaFuncionConEmpresa);
        }
    }
}
