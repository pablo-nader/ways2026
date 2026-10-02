using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class SinAcentosBusqueda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sin tablas ni columnas: solo la extensión unaccent y un envoltorio IMMUTABLE.
            // unaccent() es STABLE (depende del diccionario), así que no se puede usar en índices
            // ni se la puede tratar como pura; el envoltorio fija el diccionario por nombre
            // calificado y la declara IMMUTABLE, que es lo que necesita un índice funcional
            // futuro. unaccent es una extensión "trusted": la crea el dueño del esquema
            // (ways_owner, que corre las migraciones) sin ser superusuario. EXECUTE sobre una
            // función nueva ya es de PUBLIC, o sea que ways_app puede llamarla sin GRANT.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.sin_acentos(text)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE PARALLEL SAFE STRICT
                AS $$ SELECT public.unaccent('public.unaccent'::regdictionary, $1) $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.sin_acentos(text);");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS unaccent;");
        }
    }
}
