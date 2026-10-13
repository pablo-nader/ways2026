using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ImporteEnUnidadDeVenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Va solo en su migración: Postgres prohíbe usar un valor de enum agregado dentro de la transacción que
            // lo agregó, y los CHECK de la migración siguiente (TeclaDeAtajoEnArticulos) lo nombran. Es SQL directo y
            // no el AlterDatabase que genera EF porque este valor no se puede quitar: un Down seguido de un Up
            // volvería a agregarlo y fallaría sin IF NOT EXISTS. El valor es irreversible (Postgres no soporta
            // DROP VALUE), mismo criterio que RemitosEtapa17.
            migrationBuilder.Sql("ALTER TYPE unidad_venta ADD VALUE IF NOT EXISTS 'importe';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin reversa de `unidad_venta`: 'importe' queda como miembro muerto tras el rollback (mismo criterio que
            // RemitosEtapa17 con 'remito'); pedirle a EF que lo quite lanza NotSupportedException.
        }
    }
}
