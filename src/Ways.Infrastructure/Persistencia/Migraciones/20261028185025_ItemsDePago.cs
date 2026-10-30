using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ItemsDePago : Migration
    {
        /// <summary>Backfill de datos de <c>Up()</c>, expuesto para probarlo bajo <c>ways_app</c> (rls-migration-backfills).
        /// <c>items_pago</c> y <c>pagos</c> corren bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene
        /// BYPASSRLS: sin <c>SET LOCAL app.acceso = 'plataforma'</c> en el mismo bloque el INSERT ... SELECT no ve ninguna
        /// fila y reporta éxito. Un ítem por pago existente, también los dados de baja (copian <c>deleted_at</c>), con el
        /// medio y el importe de la cabecera. Idempotente: no toca un pago que ya tiene ítems.</summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';

            INSERT INTO items_pago (id_tenant, id_pago, id_medio_pago, importe, referencia, created_at, updated_at, deleted_at)
            SELECT p.id_tenant, p.id_pago, p.id_medio_pago, p.importe, NULL, p.created_at, p.updated_at, p.deleted_at
            FROM pagos p
            WHERE NOT EXISTS (
                SELECT 1 FROM items_pago i
                WHERE i.id_pago = p.id_pago AND i.id_tenant = p.id_tenant);
            """;

        /// <summary>Invariante de cabecera: si un pago tiene al menos un ítem vivo (<c>deleted_at IS NULL</c>), la suma de
        /// esos ítems es igual a <c>pagos.importe</c>. Un pago sin ítems vivos no se controla (los dados de baja del
        /// backfill llevan el <c>deleted_at</c> de la cabecera). Lo valida un constraint trigger DIFERIDO en las dos tablas
        /// para que el pago y sus ítems puedan escribirse en cualquier orden dentro de la misma transacción; el error sale
        /// como 23514 con <c>CONSTRAINT = tg_items_pago_suma_igual_importe</c>, que es lo que traduce
        /// <c>ManejadorDeErrores</c>. Se crea DESPUÉS del backfill.</summary>
        public const string SqlDelTriggerDeSuma =
            """
            CREATE FUNCTION fn_items_pago_exigir_suma(p_id_pago integer, p_id_tenant integer) RETURNS void
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_importe numeric;
                v_suma numeric;
                v_cantidad integer;
            BEGIN
                SELECT importe INTO v_importe FROM pagos WHERE id_pago = p_id_pago AND id_tenant = p_id_tenant;
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT count(*), COALESCE(sum(importe), 0) INTO v_cantidad, v_suma
                FROM items_pago
                WHERE id_pago = p_id_pago AND id_tenant = p_id_tenant AND deleted_at IS NULL;

                IF v_cantidad > 0 AND v_suma <> v_importe THEN
                    RAISE EXCEPTION 'La suma de las lineas del pago % (%) no coincide con su importe (%).',
                        p_id_pago, v_suma, v_importe
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'tg_items_pago_suma_igual_importe';
                END IF;
            END
            $fn$;

            CREATE FUNCTION fn_items_pago_validar_suma() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_TABLE_NAME = 'pagos' THEN
                    PERFORM fn_items_pago_exigir_suma(NEW.id_pago, NEW.id_tenant);
                    RETURN NULL;
                END IF;

                IF TG_OP <> 'DELETE' THEN
                    PERFORM fn_items_pago_exigir_suma(NEW.id_pago, NEW.id_tenant);
                END IF;

                IF TG_OP = 'DELETE'
                   OR (TG_OP = 'UPDATE' AND (OLD.id_pago, OLD.id_tenant) IS DISTINCT FROM (NEW.id_pago, NEW.id_tenant)) THEN
                    PERFORM fn_items_pago_exigir_suma(OLD.id_pago, OLD.id_tenant);
                END IF;

                RETURN NULL;
            END
            $fn$;

            CREATE CONSTRAINT TRIGGER tg_items_pago_suma_igual_importe
                AFTER INSERT OR UPDATE OR DELETE ON items_pago
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION fn_items_pago_validar_suma();

            CREATE CONSTRAINT TRIGGER tg_pagos_importe_igual_suma_items
                AFTER UPDATE OF importe ON pagos
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD.importe IS DISTINCT FROM NEW.importe)
                EXECUTE FUNCTION fn_items_pago_validar_suma();
            """;

        public const string SqlDeBajaDelTrigger =
            """
            DROP TRIGGER IF EXISTS tg_pagos_importe_igual_suma_items ON pagos;
            DROP TRIGGER IF EXISTS tg_items_pago_suma_igual_importe ON items_pago;
            DROP FUNCTION IF EXISTS fn_items_pago_validar_suma();
            DROP FUNCTION IF EXISTS fn_items_pago_exigir_suma(integer, integer);
            """;
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "items_pago",
                columns: table => new
                {
                    id_item_pago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_pago = table.Column<int>(type: "integer", nullable: false),
                    id_medio_pago = table.Column<int>(type: "integer", nullable: false),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    referencia = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items_pago", x => x.id_item_pago);
                    table.CheckConstraint("ck_items_pago_importe_positivo", "importe > 0");
                    table.CheckConstraint("ck_items_pago_referencia_longitud", "referencia IS NULL OR char_length(referencia) <= 100");
                    table.ForeignKey(
                        name: "fk_items_pago_medio_pago",
                        columns: x => new { x.id_medio_pago, x.id_tenant },
                        principalTable: "medios_pago",
                        principalColumns: new[] { "id_medio_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_pago_pago",
                        columns: x => new { x.id_pago, x.id_tenant },
                        principalTable: "pagos",
                        principalColumns: new[] { "id_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_pago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_items_pago_medio_pago",
                table: "items_pago",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_pago_pago",
                table: "items_pago",
                columns: new[] { "id_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_pago_tenant",
                table: "items_pago",
                column: "id_tenant");

            migrationBuilder.HabilitarRlsDeTenant("items_pago");

            migrationBuilder.Sql(SqlDelBackfill);
            migrationBuilder.Sql(SqlDelTriggerDeSuma);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDeBajaDelTrigger);

            migrationBuilder.DropTable(
                name: "items_pago");
        }
    }
}
