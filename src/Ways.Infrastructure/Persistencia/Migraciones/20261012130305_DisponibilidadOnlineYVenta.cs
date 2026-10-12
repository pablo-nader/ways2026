using Microsoft.EntityFrameworkCore.Migrations;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DisponibilidadOnlineYVenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // La policy de lectura pública de articulos mira publicado_web: Postgres no deja borrar la columna
            // mientras exista, así que se baja primero y se recrea al final con el predicado nuevo.
            migrationBuilder.QuitarLecturaPublica("articulos");

            migrationBuilder.DropIndex(
                name: "ix_articulos_publicado_web",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "publicado_web",
                table: "articulos");

            migrationBuilder.AddColumn<bool>(
                name: "disponible_online",
                table: "ofertas",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "disponible_online",
                table: "combos",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "disponible_online",
                table: "articulos",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "disponible_venta",
                table: "articulos",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "ix_articulos_disponible_online",
                table: "articulos",
                column: "id_tenant",
                filter: "disponible_online AND disponible_venta AND activo AND deleted_at IS NULL");

            // Sin backfill: las tres columnas nacen en true (default constante), así que toda fila existente queda
            // disponible sin escribir un solo dato. Por eso la migración no lleva SET LOCAL (rls-migration-backfills
            // aplica solo a las que escriben filas) y publicado_web se descarta en vez de renombrarse: su valor
            // viejo (false para casi todo) no es el que se quiere conservar.
            //
            // Lectura pública (modo publico, SELECT únicamente): articulos ahora exige además disponible a la
            // venta; ofertas y combos suman su policy con el mismo criterio (disponible online, activa, viva), y
            // sus tablas hijas (ofertas_listas, combos_componentes) solo el tenant: no tienen baja lógica y el
            // padre ya decide qué se ve, porque la consulta siempre entra por el id de una oferta o un combo que
            // sí se ve.
            migrationBuilder.HabilitarLecturaPublica(
                "articulos", "disponible_online AND disponible_venta AND activo AND deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("ofertas", "disponible_online AND activo AND deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("ofertas_listas");
            migrationBuilder.HabilitarLecturaPublica("combos", "disponible_online AND activo AND deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("combos_componentes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.QuitarLecturaPublica("combos_componentes");
            migrationBuilder.QuitarLecturaPublica("combos");
            migrationBuilder.QuitarLecturaPublica("ofertas_listas");
            migrationBuilder.QuitarLecturaPublica("ofertas");
            migrationBuilder.QuitarLecturaPublica("articulos");

            migrationBuilder.DropIndex(
                name: "ix_articulos_disponible_online",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "disponible_online",
                table: "ofertas");

            migrationBuilder.DropColumn(
                name: "disponible_online",
                table: "combos");

            migrationBuilder.DropColumn(
                name: "disponible_online",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "disponible_venta",
                table: "articulos");

            migrationBuilder.AddColumn<bool>(
                name: "publicado_web",
                table: "articulos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_articulos_publicado_web",
                table: "articulos",
                column: "id_tenant",
                filter: "publicado_web AND activo AND deleted_at IS NULL");

            migrationBuilder.HabilitarLecturaPublica("articulos", "publicado_web AND activo AND deleted_at IS NULL");
        }
    }
}
