using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class UsuarioQueDesconectoMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_usuario_desconecto",
                table: "conexiones_mercadopago",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_conexiones_mercadopago_usuario_desconecto",
                table: "conexiones_mercadopago",
                column: "id_usuario_desconecto");

            migrationBuilder.AddForeignKey(
                name: "fk_conexiones_mercadopago_usuario_desconecto",
                table: "conexiones_mercadopago",
                column: "id_usuario_desconecto",
                principalTable: "usuarios",
                principalColumn: "id_usuario",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_conexiones_mercadopago_usuario_desconecto",
                table: "conexiones_mercadopago");

            migrationBuilder.DropIndex(
                name: "ix_conexiones_mercadopago_usuario_desconecto",
                table: "conexiones_mercadopago");

            migrationBuilder.DropColumn(
                name: "id_usuario_desconecto",
                table: "conexiones_mercadopago");
        }
    }
}
