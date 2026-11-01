using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DebitosDePagoEnCuenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El valor nuevo del enum va con ADD VALUE IF NOT EXISTS en vez del AlterDatabase que genera EF: Postgres no soporta
            // DROP VALUE, así que tras un Down el valor sigue ahí y un Up posterior fallaría. Ningún statement de esta migración
            // nombra 'pago' (Postgres no deja usar un valor en la transacción que lo agregó): la CHECK que lo nombra está en
            // ChecksDeDebitosDePagoEnCuenta.
            migrationBuilder.Sql("ALTER TYPE tipo_movimiento_cuenta ADD VALUE IF NOT EXISTS 'pago';");

            migrationBuilder.AddColumn<int>(
                name: "id_pago",
                table: "movimientos_cuenta",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_pago",
                table: "movimientos_cuenta",
                columns: new[] { "id_pago", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_cuenta_pago",
                table: "movimientos_cuenta",
                columns: new[] { "id_pago", "id_tenant" },
                principalTable: "pagos",
                principalColumns: new[] { "id_pago", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_cuenta_pago",
                table: "movimientos_cuenta");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_cuenta_pago",
                table: "movimientos_cuenta");

            migrationBuilder.DropColumn(
                name: "id_pago",
                table: "movimientos_cuenta");
        }
    }
}
