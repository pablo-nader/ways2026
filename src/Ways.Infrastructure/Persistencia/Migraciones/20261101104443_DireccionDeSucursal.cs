using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DireccionDeSucursal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "altura",
                table: "sucursales",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "calle",
                table: "sucursales",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ciudad",
                table: "sucursales",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "codigo_postal",
                table: "sucursales",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "latitud",
                table: "sucursales",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "longitud",
                table: "sucursales",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provincia",
                table: "sucursales",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "referencia",
                table: "sucursales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sucursales_coordenadas_completas",
                table: "sucursales",
                sql: "(latitud IS NULL) = (longitud IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sucursales_latitud",
                table: "sucursales",
                sql: "latitud BETWEEN -90 AND 90");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sucursales_longitud",
                table: "sucursales",
                sql: "longitud BETWEEN -180 AND 180");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sucursales_coordenadas_completas",
                table: "sucursales");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sucursales_latitud",
                table: "sucursales");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sucursales_longitud",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "altura",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "calle",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "ciudad",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "codigo_postal",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "latitud",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "longitud",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "provincia",
                table: "sucursales");

            migrationBuilder.DropColumn(
                name: "referencia",
                table: "sucursales");
        }
    }
}
