using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class NotasDeCreditoDeCompra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_comprobante_compra_asociado",
                table: "comprobantes_compra",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_compra_comprobante_asociado",
                table: "comprobantes_compra",
                columns: new[] { "id_comprobante_compra_asociado", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_comprobantes_compra_comprobante_asociado",
                table: "comprobantes_compra",
                columns: new[] { "id_comprobante_compra_asociado", "id_tenant" },
                principalTable: "comprobantes_compra",
                principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // Notas de crédito de compra: signo -1, entran al libro IVA y sacan stock. Mismo mecanismo que C-RM en
            // ComprasRemitoYAlicuotas: el seeder solo siembra con la tabla vacía, así que una base ya migrada no
            // recibiría estos tipos de otro modo, y el guard EXISTS evita que una base genuinamente vacía quede con
            // tres filas y el seeder deje de verla como vacía. El catálogo es global pero tiene RLS de escritura de
            // plataforma: el GUC va dentro del mismo bloque (rls-migration-backfills), porque `dotnet ef database
            // update` no pasa por el interceptor de tenant. Idempotente por NOT EXISTS sobre el código.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';
                INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, registra_libro_iva, activo, created_at, updated_at)
                SELECT 'compra', v.codigo, v.nombre, v.letra, -1::smallint, v.discrimina_iva, false, true, true, true, now(), now()
                  FROM (VALUES
                        ('C-NCA', 'Nota de crédito A de compra', 'A', true),
                        ('C-NCB', 'Nota de crédito B de compra', 'B', false),
                        ('C-NCC', 'Nota de crédito C de compra', 'C', false)
                       ) AS v(codigo, nombre, letra, discrimina_iva)
                 WHERE EXISTS (SELECT 1 FROM tipos_comprobante)
                   AND NOT EXISTS (SELECT 1 FROM tipos_comprobante t WHERE t.codigo = v.codigo);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Un tipo con comprobantes no se puede borrar (FK): se desactiva, como C-RM.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';
                UPDATE tipos_comprobante SET activo = false WHERE codigo IN ('C-NCA', 'C-NCB', 'C-NCC');
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_comprobantes_compra_comprobante_asociado",
                table: "comprobantes_compra");

            migrationBuilder.DropIndex(
                name: "ix_comprobantes_compra_comprobante_asociado",
                table: "comprobantes_compra");

            migrationBuilder.DropColumn(
                name: "id_comprobante_compra_asociado",
                table: "comprobantes_compra");
        }
    }
}
