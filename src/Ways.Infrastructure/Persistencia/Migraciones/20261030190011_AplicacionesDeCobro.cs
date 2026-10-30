using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AplicacionesDeCobro : Migration
    {
        /// <summary>Invariantes de las aplicaciones de cobros a ventas (E7-S2), como constraint triggers DIFERIDOS: el
        /// recibo, sus cobros y sus aplicaciones pueden escribirse en cualquier orden dentro de la misma transacción. Todos
        /// fallan con 23514 y un <c>CONSTRAINT</c> por regla, que es lo que traduce <c>ManejadorDeErrores</c>:
        /// <list type="bullet">
        /// <item><c>tg_aplicaciones_cobro_comprobantes_validos</c>: el comprobante del cobro es un <c>RC</c>; la venta no
        /// es un <c>RC</c>, tiene signo positivo y no es un espejo fiscal; ninguno de los dos está anulado.</item>
        /// <item><c>tg_aplicaciones_cobro_no_excede_cobro</c>: la suma de las aplicaciones vivas de un recibo no supera su
        /// neto cobrado (cobros vivos, importe menos vuelto).</item>
        /// <item><c>tg_aplicaciones_cobro_no_excede_venta</c>: la suma de las aplicaciones vivas de una venta no supera su
        /// parte en cuenta corriente (cobros vivos con un medio de comportamiento cuenta corriente).</item>
        /// <item><c>tg_aplicaciones_cobro_mismo_cliente</c>: el recibo y la venta son del mismo cliente.</item>
        /// </list>
        /// Disparan en las tres operaciones sobre <c>aplicaciones_cobro</c>, en el UPDATE de
        /// <c>estado</c>/<c>id_cliente</c>/<c>id_tipo_comprobante</c> de <c>comprobantes_venta</c> (anular sin liberar las
        /// aplicaciones falla al comitear) y en el UPDATE de <c>importe</c>/<c>vuelto</c>/<c>id_medio_pago</c>/
        /// <c>id_comprobante_venta</c>/<c>deleted_at</c> o el DELETE de <c>cobros</c>. El INSERT de cobros no dispara: los
        /// cobros de un comprobante nacen en la transacción que lo crea, y una aplicación escrita en esa misma transacción
        /// se valida al comitear con todos los cobros ya insertados. Sin backfill: los recibos anteriores quedan sin
        /// aplicar.</summary>
        public const string SqlDeLosTriggers =
            """
            CREATE FUNCTION fn_aplicaciones_cobro_exigir_cobro(p_id_comprobante integer, p_id_tenant integer) RETURNS void
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_estado text;
                v_id_cliente integer;
                v_codigo text;
                v_suma numeric;
                v_cantidad integer;
                v_neto numeric;
            BEGIN
                SELECT c.estado::text, c.id_cliente, t.codigo INTO v_estado, v_id_cliente, v_codigo
                FROM comprobantes_venta c
                JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante
                WHERE c.id_comprobante_venta = p_id_comprobante AND c.id_tenant = p_id_tenant;
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT count(*), COALESCE(sum(importe), 0) INTO v_cantidad, v_suma
                FROM aplicaciones_cobro
                WHERE id_comprobante_cobro = p_id_comprobante AND id_tenant = p_id_tenant AND deleted_at IS NULL;

                IF v_cantidad = 0 THEN
                    RETURN;
                END IF;

                IF v_codigo <> 'RC' OR v_estado <> 'emitido' THEN
                    RAISE EXCEPTION 'El comprobante % (%, %) no admite aplicar su cobro.', p_id_comprobante, v_codigo, v_estado
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_cobro_comprobantes_validos';
                END IF;

                SELECT COALESCE(sum(importe - vuelto), 0) INTO v_neto
                FROM cobros
                WHERE id_comprobante_venta = p_id_comprobante AND id_tenant = p_id_tenant AND deleted_at IS NULL;

                IF v_suma > v_neto THEN
                    RAISE EXCEPTION 'Las aplicaciones del recibo % (%) superan lo cobrado (%).', p_id_comprobante, v_suma, v_neto
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_cobro_no_excede_cobro';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM aplicaciones_cobro a
                    JOIN comprobantes_venta v ON v.id_comprobante_venta = a.id_comprobante_venta AND v.id_tenant = a.id_tenant
                    WHERE a.id_comprobante_cobro = p_id_comprobante AND a.id_tenant = p_id_tenant AND a.deleted_at IS NULL
                      AND v.id_cliente <> v_id_cliente) THEN
                    RAISE EXCEPTION 'El recibo % tiene aplicaciones a ventas de otro cliente.', p_id_comprobante
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_cobro_mismo_cliente';
                END IF;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_cobro_exigir_venta(p_id_comprobante integer, p_id_tenant integer) RETURNS void
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_estado text;
                v_id_cliente integer;
                v_es_espejo boolean;
                v_codigo text;
                v_signo smallint;
                v_suma numeric;
                v_cantidad integer;
                v_parte_cc numeric;
            BEGIN
                SELECT c.estado::text, c.id_cliente, c.id_venta_origen IS NOT NULL, t.codigo, t.signo
                INTO v_estado, v_id_cliente, v_es_espejo, v_codigo, v_signo
                FROM comprobantes_venta c
                JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante
                WHERE c.id_comprobante_venta = p_id_comprobante AND c.id_tenant = p_id_tenant;
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT count(*), COALESCE(sum(importe), 0) INTO v_cantidad, v_suma
                FROM aplicaciones_cobro
                WHERE id_comprobante_venta = p_id_comprobante AND id_tenant = p_id_tenant AND deleted_at IS NULL;

                IF v_cantidad = 0 THEN
                    RETURN;
                END IF;

                IF v_codigo = 'RC' OR v_signo <= 0 OR v_es_espejo OR v_estado <> 'emitido' THEN
                    RAISE EXCEPTION 'El comprobante % (%, %) no es una venta que admita aplicaciones.',
                        p_id_comprobante, v_codigo, v_estado
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_cobro_comprobantes_validos';
                END IF;

                SELECT COALESCE(sum(co.importe), 0) INTO v_parte_cc
                FROM cobros co
                JOIN medios_pago m ON m.id_medio_pago = co.id_medio_pago AND m.id_tenant = co.id_tenant
                WHERE co.id_comprobante_venta = p_id_comprobante AND co.id_tenant = p_id_tenant AND co.deleted_at IS NULL
                  AND m.comportamiento = 'cuenta_corriente';

                IF v_suma > v_parte_cc THEN
                    RAISE EXCEPTION 'Las aplicaciones de la venta % (%) superan su parte en cuenta corriente (%).',
                        p_id_comprobante, v_suma, v_parte_cc
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_cobro_no_excede_venta';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM aplicaciones_cobro a
                    JOIN comprobantes_venta r ON r.id_comprobante_venta = a.id_comprobante_cobro AND r.id_tenant = a.id_tenant
                    WHERE a.id_comprobante_venta = p_id_comprobante AND a.id_tenant = p_id_tenant AND a.deleted_at IS NULL
                      AND r.id_cliente <> v_id_cliente) THEN
                    RAISE EXCEPTION 'La venta % tiene aplicaciones de recibos de otro cliente.', p_id_comprobante
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_aplicaciones_cobro_mismo_cliente';
                END IF;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_cobro_validar_cobro_desde_aplicacion() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP <> 'DELETE' THEN
                    PERFORM fn_aplicaciones_cobro_exigir_cobro(NEW.id_comprobante_cobro, NEW.id_tenant);
                END IF;
                IF TG_OP = 'DELETE'
                   OR (TG_OP = 'UPDATE'
                       AND (OLD.id_comprobante_cobro, OLD.id_tenant) IS DISTINCT FROM (NEW.id_comprobante_cobro, NEW.id_tenant)) THEN
                    PERFORM fn_aplicaciones_cobro_exigir_cobro(OLD.id_comprobante_cobro, OLD.id_tenant);
                END IF;
                RETURN NULL;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_cobro_validar_venta_desde_aplicacion() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP <> 'DELETE' THEN
                    PERFORM fn_aplicaciones_cobro_exigir_venta(NEW.id_comprobante_venta, NEW.id_tenant);
                END IF;
                IF TG_OP = 'DELETE'
                   OR (TG_OP = 'UPDATE'
                       AND (OLD.id_comprobante_venta, OLD.id_tenant) IS DISTINCT FROM (NEW.id_comprobante_venta, NEW.id_tenant)) THEN
                    PERFORM fn_aplicaciones_cobro_exigir_venta(OLD.id_comprobante_venta, OLD.id_tenant);
                END IF;
                RETURN NULL;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_cobro_validar_desde_comprobante() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                PERFORM fn_aplicaciones_cobro_exigir_cobro(NEW.id_comprobante_venta, NEW.id_tenant);
                PERFORM fn_aplicaciones_cobro_exigir_venta(NEW.id_comprobante_venta, NEW.id_tenant);
                RETURN NULL;
            END
            $fn$;

            CREATE FUNCTION fn_aplicaciones_cobro_validar_desde_cobros() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP <> 'DELETE' THEN
                    PERFORM fn_aplicaciones_cobro_exigir_cobro(NEW.id_comprobante_venta, NEW.id_tenant);
                    PERFORM fn_aplicaciones_cobro_exigir_venta(NEW.id_comprobante_venta, NEW.id_tenant);
                END IF;
                IF TG_OP = 'DELETE'
                   OR (TG_OP = 'UPDATE'
                       AND (OLD.id_comprobante_venta, OLD.id_tenant) IS DISTINCT FROM (NEW.id_comprobante_venta, NEW.id_tenant)) THEN
                    PERFORM fn_aplicaciones_cobro_exigir_cobro(OLD.id_comprobante_venta, OLD.id_tenant);
                    PERFORM fn_aplicaciones_cobro_exigir_venta(OLD.id_comprobante_venta, OLD.id_tenant);
                END IF;
                RETURN NULL;
            END
            $fn$;

            CREATE CONSTRAINT TRIGGER tg_aplicaciones_cobro_no_excede_cobro
                AFTER INSERT OR UPDATE OR DELETE ON aplicaciones_cobro
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION fn_aplicaciones_cobro_validar_cobro_desde_aplicacion();

            CREATE CONSTRAINT TRIGGER tg_aplicaciones_cobro_no_excede_venta
                AFTER INSERT OR UPDATE OR DELETE ON aplicaciones_cobro
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION fn_aplicaciones_cobro_validar_venta_desde_aplicacion();

            CREATE CONSTRAINT TRIGGER tg_comprobantes_venta_admiten_aplicaciones_cobro
                AFTER UPDATE OF estado, id_cliente, id_tipo_comprobante ON comprobantes_venta
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD.estado IS DISTINCT FROM NEW.estado
                      OR OLD.id_cliente IS DISTINCT FROM NEW.id_cliente
                      OR OLD.id_tipo_comprobante IS DISTINCT FROM NEW.id_tipo_comprobante)
                EXECUTE FUNCTION fn_aplicaciones_cobro_validar_desde_comprobante();

            CREATE CONSTRAINT TRIGGER tg_cobros_cubren_aplicaciones_cobro
                AFTER UPDATE OF importe, vuelto, id_medio_pago, id_comprobante_venta, deleted_at OR DELETE ON cobros
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION fn_aplicaciones_cobro_validar_desde_cobros();
            """;

        public const string SqlDeBajaDeLosTriggers =
            """
            DROP TRIGGER IF EXISTS tg_cobros_cubren_aplicaciones_cobro ON cobros;
            DROP TRIGGER IF EXISTS tg_comprobantes_venta_admiten_aplicaciones_cobro ON comprobantes_venta;
            DROP TRIGGER IF EXISTS tg_aplicaciones_cobro_no_excede_venta ON aplicaciones_cobro;
            DROP TRIGGER IF EXISTS tg_aplicaciones_cobro_no_excede_cobro ON aplicaciones_cobro;
            DROP FUNCTION IF EXISTS fn_aplicaciones_cobro_validar_desde_cobros();
            DROP FUNCTION IF EXISTS fn_aplicaciones_cobro_validar_desde_comprobante();
            DROP FUNCTION IF EXISTS fn_aplicaciones_cobro_validar_venta_desde_aplicacion();
            DROP FUNCTION IF EXISTS fn_aplicaciones_cobro_validar_cobro_desde_aplicacion();
            DROP FUNCTION IF EXISTS fn_aplicaciones_cobro_exigir_venta(integer, integer);
            DROP FUNCTION IF EXISTS fn_aplicaciones_cobro_exigir_cobro(integer, integer);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aplicaciones_cobro",
                columns: table => new
                {
                    id_aplicacion_cobro = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_comprobante_cobro = table.Column<int>(type: "integer", nullable: false),
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: false),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aplicaciones_cobro", x => x.id_aplicacion_cobro);
                    table.CheckConstraint("ck_aplicaciones_cobro_importe_positivo", "importe > 0");
                    table.ForeignKey(
                        name: "fk_aplicaciones_cobro_cobro",
                        columns: x => new { x.id_comprobante_cobro, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aplicaciones_cobro_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aplicaciones_cobro_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aplicaciones_cobro_venta",
                        columns: x => new { x.id_comprobante_venta, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_cobro_cobro",
                table: "aplicaciones_cobro",
                columns: new[] { "id_comprobante_cobro", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_cobro_empleado",
                table: "aplicaciones_cobro",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_cobro_tenant",
                table: "aplicaciones_cobro",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_aplicaciones_cobro_venta",
                table: "aplicaciones_cobro",
                columns: new[] { "id_comprobante_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_aplicaciones_cobro_cobro_venta",
                table: "aplicaciones_cobro",
                columns: new[] { "id_comprobante_cobro", "id_comprobante_venta", "id_tenant" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.HabilitarRlsDeTenant("aplicaciones_cobro");

            migrationBuilder.Sql(SqlDeLosTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDeBajaDeLosTriggers);

            migrationBuilder.DropTable(
                name: "aplicaciones_cobro");
        }
    }
}
