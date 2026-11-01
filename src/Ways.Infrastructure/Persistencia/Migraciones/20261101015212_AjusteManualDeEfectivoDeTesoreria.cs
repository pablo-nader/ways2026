using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AjusteManualDeEfectivoDeTesoreria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El valor nuevo del enum va con ADD VALUE IF NOT EXISTS en vez del AlterDatabase que genera EF: Postgres
            // no soporta DROP VALUE, así que tras un Down el valor sigue ahí y un Up posterior fallaría.
            migrationBuilder.Sql("ALTER TYPE tipo_movimiento_tesoreria ADD VALUE IF NOT EXISTS 'ajuste_manual';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Postgres no puede quitar una etiqueta de un enum: el Down deja el valor.
        }
    }
}
