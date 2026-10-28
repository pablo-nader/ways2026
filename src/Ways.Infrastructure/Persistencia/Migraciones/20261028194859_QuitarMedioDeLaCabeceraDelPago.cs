using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <summary>E2-S3b: el medio de pago deja la cabecera del pago y vive solo en <c>items_pago</c>. <c>Up</c> elimina
    /// <c>pagos.id_medio_pago</c> con su FK y su índice; <c>Down</c> los recrea y rellena la columna con el medio del ítem
    /// vivo de menor id de cada pago.</summary>
    public partial class QuitarMedioDeLaCabeceraDelPago : Migration
    {
        /// <summary>Backfill de <c>Down()</c>, expuesto para probarlo bajo <c>ways_app</c> (rls-migration-backfills).
        /// <c>pagos</c> e <c>items_pago</c> corren bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS:
        /// sin <c>SET LOCAL app.acceso = 'plataforma'</c> en el mismo bloque el UPDATE no ve ninguna fila y reporta éxito.
        /// El medio sale del ítem vivo de menor id; si el pago no tiene ítems vivos, del ítem de menor id; y si no tiene
        /// ningún ítem (no lo produce ningún escritor), del medio de menor id de su tenant, para que la columna pueda
        /// volver a ser NOT NULL. Idempotente: solo toca los pagos con la columna vacía.</summary>
        public const string SqlDelRespaldoDeLaBaja =
            """
            SET LOCAL app.acceso = 'plataforma';

            UPDATE pagos p
            SET id_medio_pago = COALESCE(
                (SELECT i.id_medio_pago
                 FROM items_pago i
                 WHERE i.id_pago = p.id_pago AND i.id_tenant = p.id_tenant
                 ORDER BY (i.deleted_at IS NULL) DESC, i.id_item_pago
                 LIMIT 1),
                (SELECT m.id_medio_pago
                 FROM medios_pago m
                 WHERE m.id_tenant = p.id_tenant
                 ORDER BY m.id_medio_pago
                 LIMIT 1))
            WHERE p.id_medio_pago IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_pagos_medio_pago",
                table: "pagos");

            migrationBuilder.DropIndex(
                name: "ix_pagos_medio_pago",
                table: "pagos");

            migrationBuilder.DropColumn(
                name: "id_medio_pago",
                table: "pagos");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_medio_pago",
                table: "pagos",
                type: "integer",
                nullable: true);

            // rls-migration-backfills: el SET LOCAL de plataforma va dentro del mismo bloque.
            migrationBuilder.Sql(SqlDelRespaldoDeLaBaja);

            migrationBuilder.AlterColumn<int>(
                name: "id_medio_pago",
                table: "pagos",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_pagos_medio_pago",
                table: "pagos",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_pagos_medio_pago",
                table: "pagos",
                columns: new[] { "id_medio_pago", "id_tenant" },
                principalTable: "medios_pago",
                principalColumns: new[] { "id_medio_pago", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
