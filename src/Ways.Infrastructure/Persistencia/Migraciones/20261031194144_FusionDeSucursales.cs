using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class FusionDeSucursales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_sucursal_fusion",
                table: "sucursales",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sucursales_id_sucursal_fusion_id_empresa_id_tenant",
                table: "sucursales",
                columns: new[] { "id_sucursal_fusion", "id_empresa", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_sucursales_fusion",
                table: "sucursales",
                columns: new[] { "id_sucursal_fusion", "id_empresa", "id_tenant" },
                principalTable: "sucursales",
                principalColumns: new[] { "id_sucursal", "id_empresa", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_sucursales_fusion",
                table: "sucursales");

            migrationBuilder.DropIndex(
                name: "IX_sucursales_id_sucursal_fusion_id_empresa_id_tenant",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "id_sucursal_fusion",
                table: "sucursales");
        }
    }
}
