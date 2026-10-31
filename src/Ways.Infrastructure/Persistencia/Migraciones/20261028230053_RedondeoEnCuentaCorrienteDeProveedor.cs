using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RedondeoEnCuentaCorrienteDeProveedor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El valor nuevo del enum va con ADD VALUE IF NOT EXISTS en vez del AlterDatabase que genera EF: Postgres no
            // soporta DROP VALUE, así que tras un Down el valor sigue ahí y un Up posterior fallaría. Ningún statement de
            // esta migración nombra 'redondeo' (Postgres no deja usar un valor en la transacción que lo agregó): el CHECK
            // que lo nombra está en RedondeoUnDestino.
            migrationBuilder.Sql("ALTER TYPE tipo_movimiento_cc_proveedor ADD VALUE IF NOT EXISTS 'redondeo';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Postgres no puede quitar un valor de un enum: 'redondeo' queda en el tipo.
        }
    }
}
