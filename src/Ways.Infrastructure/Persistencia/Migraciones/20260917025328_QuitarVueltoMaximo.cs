using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class QuitarVueltoMaximo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migración de datos, sin cambio de esquema (decisión del dueño, 2026-09-16):
            // `vuelto_maximo` dejó de ser un parámetro conocido (PRs #205/#211 reemplazaron el
            // validador que lo consumía por la regla de billetes formables) y se sacó del
            // registro `ParametroConocido` en el mismo cambio — esta migración borra cualquier
            // fila remanente que un tenant haya llegado a configurar, en cualquier alcance
            // (id_punto_venta NULL = empresa, o con valor = punto de venta).
            //
            // `parametros` corre bajo FORCE ROW LEVEL SECURITY (misma migración `Parametros`
            // que creó la tabla) y el rol de aplicación no tiene BYPASSRLS en Producción
            // (InicializadorDeBaseDeDatos.VerificarRolSinBypassAsync) — un DELETE plano solo
            // vería las filas del tenant de la sesión que corre `dotnet ef database update` (o
            // ninguna, si esa sesión no tiene contexto de tenant) y reportaría éxito sin haber
            // tocado el resto. El mismo patrón que el backfill de
            // `CostoCongeladoEnVentaEtapa9`: `SET LOCAL app.acceso = 'plataforma'` hace que
            // `app_es_plataforma()` habilite la policy para cualquier `id_tenant`, así que el
            // DELETE alcanza todos los tenants en una sola pasada. Idempotente por
            // construcción: una corrida repetida no encuentra filas y no hace nada.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                DELETE FROM parametros WHERE clave = 'vuelto_maximo';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op deliberado: `vuelto_maximo` nunca tuvo una fila default sembrada por una
            // migración (a diferencia de los catálogos globales, `parametros` nunca tuvo un
            // `HasData`/INSERT de aprovisionamiento — el "default" siempre fue el
            // `ValorPorDefecto` declarado en `ParametroConocido`, resuelto en memoria cuando no
            // hay fila, nunca persistido). Las filas que este Up() borró eran valores que un
            // tenant había configurado explícitamente vía PUT — ese dato es irrecuperable una
            // vez borrado, y Down() no puede reconstruirlo. Además, para esta fecha
            // `ParametroConocido` ya no conoce la clave `vuelto_maximo` (mismo cambio que esta
            // migración) y `EstablecerAsync`/`ResolverAsync` la rechazan con 400
            // `parametro_desconocido` — revertir el esquema/registro requeriría revertir también
            // el commit de código, fuera del alcance de una migración de datos.
        }
    }
}
