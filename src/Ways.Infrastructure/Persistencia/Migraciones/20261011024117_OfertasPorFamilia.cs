using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class OfertasPorFamilia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ofertas_alcance_exclusivo",
                table: "ofertas");

            migrationBuilder.AddColumn<int>(
                name: "id_familia",
                table: "ofertas",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_familia",
                table: "ofertas",
                columns: new[] { "id_familia", "id_tenant" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ofertas_alcance_exclusivo",
                table: "ofertas",
                sql: "num_nonnulls(id_articulo, id_grupo, id_categoria, id_familia) = 1");

            migrationBuilder.AddForeignKey(
                name: "fk_ofertas_familia",
                table: "ofertas",
                columns: new[] { "id_familia", "id_tenant" },
                principalTable: "familias",
                principalColumns: new[] { "id_familia", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ofertas_familia",
                table: "ofertas");

            migrationBuilder.DropIndex(
                name: "ix_ofertas_familia",
                table: "ofertas");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ofertas_alcance_exclusivo",
                table: "ofertas");

            migrationBuilder.DropColumn(
                name: "id_familia",
                table: "ofertas");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ofertas_alcance_exclusivo",
                table: "ofertas",
                sql: "num_nonnulls(id_articulo, id_grupo, id_categoria) = 1");
        }
    }
}
