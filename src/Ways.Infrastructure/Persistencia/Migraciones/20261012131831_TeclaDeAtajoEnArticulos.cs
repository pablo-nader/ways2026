using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class TeclaDeAtajoEnArticulos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tecla_atajo",
                table: "articulos",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_articulos_tecla_atajo",
                table: "articulos",
                columns: new[] { "id_tenant", "tecla_atajo" },
                unique: true,
                filter: "deleted_at IS NULL AND tecla_atajo IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_articulos_importe_no_acumula",
                table: "articulos",
                sql: "unidad_venta <> 'importe' OR acumula_en_venta = false");

            migrationBuilder.AddCheckConstraint(
                name: "ck_articulos_tecla_atajo",
                table: "articulos",
                sql: "tecla_atajo IN ('F5', 'F6', 'F7', 'F8')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_articulos_tecla_atajo_solo_importe",
                table: "articulos",
                sql: "tecla_atajo IS NULL OR unidad_venta = 'importe'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_articulos_tecla_atajo",
                table: "articulos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_articulos_importe_no_acumula",
                table: "articulos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_articulos_tecla_atajo",
                table: "articulos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_articulos_tecla_atajo_solo_importe",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "tecla_atajo",
                table: "articulos");
        }
    }
}
