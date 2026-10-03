using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ComprasRemitoYAlicuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "registra_libro_iva",
                table: "tipos_comprobante",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Las tres facturas de compra son los únicos tipos que entran al libro IVA. Los
            // catálogos globales también tienen RLS de escritura-plataforma: el GUC va dentro del
            // mismo bloque (rls-migration-backfills), porque la ruta de `dotnet ef database update`
            // no pasa por el interceptor de tenant.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';
                UPDATE tipos_comprobante SET registra_libro_iva = true
                 WHERE codigo IN ('C-FA', 'C-FB', 'C-FC') AND registra_libro_iva = false;
                """);

            // Remito / comprobante no fiscal. Mismo mecanismo que C-FA/C-FB/C-FC en
            // ComprasYTransferenciasEtapa8: el seeder de InicializadorDeBaseDeDatos solo siembra el
            // catálogo con la tabla vacía, así que una base ya migrada no recibiría este tipo de
            // otro modo. El guard AND EXISTS evita que una base genuinamente vacía quede con una
            // sola fila y el seeder deje de verla como vacía.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';
                INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, registra_libro_iva, activo, created_at, updated_at)
                SELECT 'compra', 'C-RM', 'Remito / comprobante no fiscal', 'X', 1::smallint, false, false, true, false, true, now(), now()
                WHERE EXISTS (SELECT 1 FROM tipos_comprobante)
                  AND NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo = 'C-RM');
                """);

            migrationBuilder.AddColumn<bool>(
                name: "discrimina_iva",
                table: "comprobantes_compra",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // El comprobante hereda el discrimina_iva de su tipo: hasta ahora era el tipo quien lo
            // decidía, así que es exactamente lo que cada compra existente ya calculó. comprobantes_compra
            // corre bajo FORCE ROW LEVEL SECURITY: sin el GUC el UPDATE toca cero filas y reporta
            // éxito (rls-migration-backfills). Idempotente: solo toca las filas que difieren.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';
                UPDATE comprobantes_compra c
                   SET discrimina_iva = t.discrimina_iva
                  FROM tipos_comprobante t
                 WHERE t.id_tipo_comprobante = c.id_tipo_comprobante
                   AND c.discrimina_iva IS DISTINCT FROM t.discrimina_iva;
                """);

            // El valor lo escribe siempre el servicio: ningún insert debe heredar un default.
            migrationBuilder.Sql("ALTER TABLE comprobantes_compra ALTER COLUMN discrimina_iva DROP DEFAULT;");

            migrationBuilder.CreateTable(
                name: "alicuotas_comprobante_compra",
                columns: table => new
                {
                    id_alicuota_comprobante_compra = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_comprobante_compra = table.Column<int>(type: "integer", nullable: false),
                    id_alicuota_iva = table.Column<int>(type: "integer", nullable: false),
                    porcentaje = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    neto = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    iva = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alicuotas_comprobante_compra", x => x.id_alicuota_comprobante_compra);
                    table.CheckConstraint("ck_alicuotas_comprobante_compra_importes_no_negativos", "neto >= 0 AND iva >= 0");
                    table.ForeignKey(
                        name: "fk_alicuotas_comprobante_compra_alicuota_iva",
                        column: x => x.id_alicuota_iva,
                        principalTable: "alicuotas_iva",
                        principalColumn: "id_alicuota_iva",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alicuotas_comprobante_compra_comprobante",
                        columns: x => new { x.id_comprobante_compra, x.id_tenant },
                        principalTable: "comprobantes_compra",
                        principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alicuotas_comprobante_compra_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alicuotas_comprobante_compra_alicuota_iva",
                table: "alicuotas_comprobante_compra",
                column: "id_alicuota_iva");

            migrationBuilder.CreateIndex(
                name: "ix_alicuotas_comprobante_compra_comprobante",
                table: "alicuotas_comprobante_compra",
                columns: new[] { "id_comprobante_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_alicuotas_comprobante_compra_tenant",
                table: "alicuotas_comprobante_compra",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_alicuotas_comprobante_compra_alicuota",
                table: "alicuotas_comprobante_compra",
                columns: new[] { "id_comprobante_compra", "id_alicuota_iva" },
                unique: true);

            migrationBuilder.HabilitarRlsDeTenant("alicuotas_comprobante_compra");

            // Las compras que ya discriminan IVA reciben su desglose a partir de los ítems, con el
            // mismo redondeo por línea con el que se calculó su iva_total: así la suma de las filas
            // coincide con el encabezado y el libro IVA lee una sola tabla. El GUC va dentro del
            // bloque (rls-migration-backfills); idempotente por el NOT EXISTS.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';
                INSERT INTO alicuotas_comprobante_compra
                    (id_tenant, id_comprobante_compra, id_alicuota_iva, porcentaje, neto, iva, created_at, updated_at)
                SELECT i.id_tenant, i.id_comprobante_compra, i.id_alicuota_iva, max(i.porcentaje_iva),
                       sum(i.total), sum(round(i.total * i.porcentaje_iva / 100, 2)), now(), now()
                  FROM items_comprobante_compra i
                  JOIN comprobantes_compra c
                    ON c.id_comprobante_compra = i.id_comprobante_compra AND c.id_tenant = i.id_tenant
                 WHERE c.discrimina_iva
                   AND i.deleted_at IS NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM alicuotas_comprobante_compra a
                        WHERE a.id_comprobante_compra = c.id_comprobante_compra)
                 GROUP BY i.id_tenant, i.id_comprobante_compra, i.id_alicuota_iva;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Desactiva en vez de borrar, para que una compra ya cargada con este tipo siga siendo
            // legible después de un rollback (mismo criterio que ComprasYTransferenciasEtapa8).
            // El GUC va dentro del mismo bloque, como en Up (rls-migration-backfills).
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';
                UPDATE tipos_comprobante SET activo = false WHERE codigo = 'C-RM';
                """);

            migrationBuilder.DropTable(
                name: "alicuotas_comprobante_compra");

            migrationBuilder.DropColumn(
                name: "registra_libro_iva",
                table: "tipos_comprobante");

            migrationBuilder.DropColumn(
                name: "discrimina_iva",
                table: "comprobantes_compra");
        }
    }
}
