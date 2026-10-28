using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ImpuestoInternoDelArticuloEsUnImporte : Migration
    {
        /// <summary>Convierte cada porcentaje al importe equivalente cuando se puede: un costo de lista sin IVA y un
        /// porcentaje distinto de cero (round(costo_lista x porcentaje / 100, 2), en la misma base del costo de lista). El
        /// resto queda en 0: con un costo de lista con IVA el costo ya era final y el porcentaje no se aplicaba.</summary>
        public const string SqlDelBackfill =
            """
            -- articulos corre bajo FORCE ROW LEVEL SECURITY: sin el GUC de plataforma el UPDATE toca 0 filas
            -- (skill rls-migration-backfills). Idempotente: solo toca filas con el importe todavía en 0.
            SET LOCAL app.acceso = 'plataforma';

            UPDATE articulos
            SET impuesto_interno = round(costo_lista * porcentaje_impuesto_interno / 100, 2)
            WHERE costo_lista IS NOT NULL
              AND NOT costo_lista_incluye_iva
              AND porcentaje_impuesto_interno <> 0
              AND impuesto_interno = 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "impuesto_interno",
                table: "articulos",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(SqlDelBackfill);

            migrationBuilder.DropColumn(
                name: "porcentaje_impuesto_interno",
                table: "articulos");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El importe no se convierte de vuelta a porcentaje: la columna vieja vuelve en 0.
            migrationBuilder.AddColumn<decimal>(
                name: "porcentaje_impuesto_interno",
                table: "articulos",
                type: "numeric(7,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.DropColumn(
                name: "impuesto_interno",
                table: "articulos");
        }
    }
}
