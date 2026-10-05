using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;

namespace Ways.Application.Familias;

/// <summary>
/// Lecturas y bloqueos de fila de la PERTENENCIA a las familias (doc 10 §3, "Familias de artículos"):
/// los pasos que vienen después del lock de membresía (<see cref="LockDeMembresiaDeFamilias"/>) en el
/// protocolo de locks de todo escritor de campos compartidos o de precios. Reciben la conexión y la
/// transacción del llamador —misma forma que <see cref="LockDeMembresiaDeFamilias"/>— y tienen que
/// ejecutarse DENTRO de esa transacción y DESPUÉS de ese lock: la pertenencia que leen es estable
/// hasta el commit solo porque el lock de membresía ya está tomado.
///
/// <para>Un escritor con una entidad rastreada por EF lee la entidad DESPUÉS de estos bloqueos, una sola
/// vez (<c>single-read-under-lock</c>): lo que devuelven es la pertenencia y los ids, nunca la fila.</para>
/// </summary>
internal static class MembresiaDeFamilias
{
    /// <summary>La familia del artículo (<c>null</c> ⇒ no es miembro) leída DENTRO de la transacción y
    /// DESPUÉS del lock de membresía — la única lectura de la pertenencia de la operación. Una proyección
    /// escalar con <c>deleted_at IS NULL</c>, nunca una entidad rastreada: la salida de la familia de
    /// "solo este" es una escritura sobre una fila que se bloquea guardada por este valor
    /// (<see cref="BloquearFilaDelArticuloAsync"/>). Si la fila no existe, o está dada de baja, es el
    /// mismo 404 que el pre-chequeo de existencia de los escritores.</summary>
    public static async Task<int?> LeerIdFamiliaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idArticulo, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_familia FROM articulos WHERE id_articulo = $1 AND id_tenant = $2 AND deleted_at IS NULL";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        if (!await lector.ReadAsync(ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe el artículo {idArticulo}.");
        }

        return lector.IsDBNull(0) ? null : lector.GetInt32(0);
    }

    /// <summary>Los miembros vivos de la familia, ascendentes por <c>id_articulo</c>, con sus filas de
    /// <c>articulos</c> bloqueadas <c>FOR NO KEY UPDATE</c> — paso (2) del orden global de locks. Un
    /// solo statement con <c>ORDER BY</c>: PostgreSQL toma los locks de fila en el orden del sort, que
    /// es el orden que evita el ciclo entre dos escritores de la misma familia. Nunca <c>FOR UPDATE</c>
    /// sobre varias filas: choca con el <c>FOR KEY SHARE</c> que toman las ventas por sus FK y puede
    /// formar un deadlock. Si una fila cambió mientras se esperaba su lock, PostgreSQL reevalúa el
    /// <c>WHERE</c> sobre la versión nueva y la descarta si ya no es miembro vivo.</summary>
    public static async Task<List<int>> BloquearMiembrosAsync(
        DbConnection conexion, DbTransaction? transaccion, int idFamilia, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_articulo FROM articulos " +
            "WHERE id_familia = $1 AND id_tenant = $2 AND deleted_at IS NULL " +
            "ORDER BY id_articulo FOR NO KEY UPDATE";

        ParametrosDeComando.Agregar(comando, idFamilia);
        ParametrosDeComando.Agregar(comando, idTenant);

        var miembros = new List<int>();
        await using var lector = await comando.ExecuteReaderAsync(ct);

        while (await lector.ReadAsync(ct))
        {
            miembros.Add(lector.GetInt32(0));
        }

        return miembros;
    }

    /// <summary>"Solo este", paso (2) del orden de locks: bloquea la fila del artículo
    /// <c>FOR NO KEY UPDATE</c> —el mismo modo que <see cref="BloquearMiembrosAsync"/>— guardada por la
    /// familia que se leyó bajo el lock de membresía y por la baja lógica. Si la fila no cumple el
    /// <c>WHERE</c> —otro escritor cambió su pertenencia o la dio de baja sin respetar el lock de
    /// membresía, también mientras se esperaba el lock de la fila— se rechaza como
    /// <c>familia_cambio</c>.</summary>
    public static async Task BloquearFilaDelArticuloAsync(
        DbConnection conexion, DbTransaction? transaccion, int idArticulo, int idFamilia, int idTenant,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT 1 FROM articulos " +
            "WHERE id_articulo = $1 AND id_tenant = $2 AND id_familia = $3 AND deleted_at IS NULL " +
            "FOR NO KEY UPDATE";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idFamilia);

        if (await comando.ExecuteScalarAsync(ct) is null)
        {
            throw ErrorDeFamiliaCambio();
        }
    }

    /// <summary>Lo que el alta de un artículo dentro de una familia necesita saber de ella: su nombre, para los
    /// mensajes, y si está activa.</summary>
    public sealed record FamiliaParaIngresar(string Nombre, bool Activa);

    /// <summary>La familia a la que va a entrar un artículo nuevo: VIVA (<c>deleted_at IS NULL</c>) y de este
    /// tenant, leída y bloqueada <c>FOR SHARE</c> en un solo statement, bajo el lock de membresía exclusivo;
    /// <c>null</c> si no existe, es de otro tenant o está dada de baja. El <c>FOR SHARE</c> —y no el
    /// <c>FOR KEY SHARE</c> de los chequeos de catálogo, que solo choca con quien borra o cambia la clave— es
    /// lo que serializa el alta con cualquier <c>UPDATE</c> de la fila de la familia, sea una baja lógica o un
    /// cambio de <c>activo</c>: todo <c>UPDATE</c> toma al menos <c>FOR NO KEY UPDATE</c>, que choca con
    /// <c>FOR SHARE</c>. Si la fila cambió mientras se esperaba su lock, PostgreSQL reevalúa el <c>WHERE</c>
    /// sobre la versión nueva y la descarta si ya no está viva.</summary>
    public static async Task<FamiliaParaIngresar?> LeerFamiliaParaIngresarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idFamilia, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT nombre, activo FROM familias " +
            "WHERE id_familia = $1 AND id_tenant = $2 AND deleted_at IS NULL " +
            "FOR SHARE";

        ParametrosDeComando.Agregar(comando, idFamilia);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        return await lector.ReadAsync(ct)
            ? new FamiliaParaIngresar(lector.GetString(0), lector.GetBoolean(1))
            : null;
    }

    /// <summary>409 <c>alcance_requerido</c>: el artículo es miembro y el cliente no eligió.
    /// <paramref name="queHayQueIndicar"/> completa la oración que nombra la familia y la cantidad de
    /// artículos vivos (<see cref="ErrorDominio"/> no lleva datos estructurados): lo que cada escritor
    /// le pide al cliente que decida. Se lee acá, solo en el camino de error, bajo el mismo lock de
    /// membresía.</summary>
    public static async Task<ErrorDominio> ErrorDeAlcanceRequeridoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idFamilia, int idTenant, string queHayQueIndicar,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT f.nombre, " +
            "(SELECT count(*) FROM articulos a WHERE a.id_familia = f.id_familia AND a.id_tenant = f.id_tenant " +
            "AND a.deleted_at IS NULL) " +
            "FROM familias f WHERE f.id_familia = $1 AND f.id_tenant = $2";

        ParametrosDeComando.Agregar(comando, idFamilia);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        // La FK compuesta de articulos garantiza que la familia de un miembro existe: sin fila,
        // GetString falla fuerte en vez de inventar un nombre.
        await lector.ReadAsync(ct);
        var nombre = lector.GetString(0);
        var cantidad = lector.GetInt64(1);
        var articulos = cantidad == 1 ? "1 artículo" : $"{cantidad} artículos";

        return ErrorDominio.Conflicto(
            "alcance_requerido",
            $"El artículo pertenece a la familia \"{nombre}\" ({articulos}): {queHayQueIndicar}");
    }

    /// <summary>409 <c>familia_cambio</c>: la pertenencia que el cliente daba por cierta ya no lo es
    /// (eligió un alcance sobre un artículo que dejó de ser miembro, o que dejó de existir mientras
    /// esta escritura esperaba el lock de su fila).</summary>
    public static ErrorDominio ErrorDeFamiliaCambio() =>
        ErrorDominio.Conflicto(
            "familia_cambio",
            "La pertenencia del artículo a su familia cambió desde que se cargó la pantalla; hay que recargar y volver a intentar.");
}
