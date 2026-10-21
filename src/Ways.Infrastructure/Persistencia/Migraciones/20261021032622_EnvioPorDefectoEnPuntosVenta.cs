using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class EnvioPorDefectoEnPuntosVenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "envio_por_defecto",
                table: "puntos_venta",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "envio_por_defecto",
                table: "puntos_venta");
        }
    }
}
