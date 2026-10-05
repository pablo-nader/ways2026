using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Application.Familias;

/// <summary>
/// Gestión de familias de artículos (doc 10 §3, "Familias de artículos"): las lecturas —listar las familias y leer
/// una—, la edición de su nombre y de su estado, y los dos cambios de pertenencia que solo sacan miembros: sacar un
/// artículo de su familia y disolver la familia. Autorización: <c>Politicas.GestionDeCatalogo</c> aplicada en la
/// capa de API, la misma puerta que el alta y la edición de artículos.
///
/// <para>Las lecturas no toman locks: la familia, sus miembros y los precios de la referencia salen de consultas
/// separadas, así que una escritura concurrente puede mostrarlos en momentos distintos. Lo que sostiene la
/// invariante de la familia son los escritores, no estas lecturas.</para>
///
/// <para>Sacar un artículo y disolver cambian la pertenencia: toman el lock de membresía EXCLUSIVO como primera
/// sentencia de su transacción (<see cref="LockDeMembresiaDeFamilias"/>), así que ningún otro escritor de familias
/// está a mitad de camino mientras ellos escriben. Ninguno cambia un campo compartido ni un precio: lo que sale de la
/// familia conserva todos sus valores.</para>
/// </summary>
public class ServicioDeFamilias(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, GuardaDeReferencias guarda)
{
    /// <summary>El tope del nombre: la columna <c>familias.nombre</c> es <c>citext</c> de 150, como
    /// <c>articulos.nombre</c>.</summary>
    private const int LargoMaximoDelNombre = 150;

    /// <summary>Las familias vivas del tenant, ordenadas por nombre. Cada una con la cantidad de miembros vivos:
    /// el filtro de baja lógica de <c>articulos</c> deja afuera a los dados de baja.</summary>
    public async Task<IReadOnlyList<FamiliaListado>> ListarAsync(CancellationToken ct = default) =>
        await db.Familias
            .OrderBy(f => f.Nombre)
            .Select(f => new FamiliaListado(f.Id, f.Nombre, f.Activo, db.Articulos.Count(a => a.IdFamilia == f.Id)))
            .ToListAsync(ct);

    /// <summary>La familia, sus miembros vivos, los trece valores compartidos del artículo de referencia (el
    /// miembro vivo de menor id) y su estado de precios en cada lista fija: lo que el alta de un artículo dentro
    /// de la familia necesita prellenar. <c>404</c> si la familia no existe, está dada de baja o es de otro tenant.</summary>
    public async Task<FamiliaDetalle> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var familia = await db.Familias.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la familia {id}.");

        var articulos = await db.Articulos.AsNoTracking().Where(a => a.IdFamilia == id).OrderBy(a => a.Id).ToListAsync(ct);

        if (articulos.Count == 0)
        {
            return new FamiliaDetalle(familia.Id, familia.Nombre, familia.Activo, [], Valores: null, Precios: []);
        }

        var referencia = articulos[0];

        var idsDeMarca = articulos.Where(a => a.IdMarca is not null).Select(a => a.IdMarca!.Value).Distinct().ToList();
        var marcasVisibles = await db.Marcas
            .Where(m => idsDeMarca.Contains(m.Id))
            .Select(m => m.Id)
            .ToHashSetAsync(ct);

        var miembros = articulos
            .Select(a => new MiembroDeFamilia(
                a.Id, a.CodigoInterno, a.Nombre,
                a.IdMarca is { } idMarca && marcasVisibles.Contains(idMarca) ? idMarca : null,
                a.Activo))
            .ToList();

        var valores = new ValoresCompartidosDeLaFamilia(
            IdArea: await db.Areas.AnyAsync(x => x.Id == referencia.IdArea, ct) ? referencia.IdArea : null,
            IdCategoria: referencia.IdCategoria is { } idCategoria
                && await db.Categorias.AnyAsync(x => x.Id == idCategoria, ct) ? idCategoria : null,
            IdGrupo: referencia.IdGrupo is { } idGrupo
                && await db.Grupos.AnyAsync(x => x.Id == idGrupo, ct) ? idGrupo : null,
            IdProveedorHabitual: referencia.IdProveedorHabitual is { } idProveedor
                && await db.Proveedores.AnyAsync(x => x.Id == idProveedor, ct) ? idProveedor : null,
            IdAlicuotaIva: referencia.IdAlicuotaIva,
            UnidadVenta: referencia.UnidadVenta,
            UnidadesPorBulto: referencia.UnidadesPorBulto,
            EsProducto: referencia.EsProducto,
            ControlaLote: referencia.ControlaLote,
            AcumulaEnVenta: referencia.AcumulaEnVenta,
            CostoLista: referencia.CostoLista,
            DescuentoProveedor: referencia.DescuentoProveedor,
            CostoNominal: referencia.CostoNominal);

        return new FamiliaDetalle(
            familia.Id, familia.Nombre, familia.Activo, miembros, valores, await EstadoDePreciosDeLaReferenciaAsync(referencia.Id, ct));
    }

    /// <summary>
    /// Renombra la familia y le cambia el estado (<see cref="EdicionFamilia"/>). Una sola transacción, sin reintento
    /// (<c>ef-retry-safe-writes</c>, forma (b), como la edición de artículos: un reintento tras un commit ambiguo
    /// leería la familia ya editada por el intento anterior, y el fallo transitorio llega al operador como
    /// <c>503 resultado_incierto</c>): el <c>FOR UPDATE</c> de la fila de la familia es lo primero, y la familia se lee
    /// UNA sola vez, después de ese lock (<c>single-read-under-lock</c>). Una familia dada de baja mientras esta edición
    /// esperaba el lock ya no se encuentra y la respuesta es el <c>404</c>, sin escribir nada.
    ///
    /// <para>No toma el lock de membresía ni bloquea ninguna fila de artículo: no cambia la pertenencia ni escribe
    /// campos compartidos ni precios. Lo que la serializa con los escritores que leen la fila de la familia —el alta de
    /// un artículo con <c>idFamilia</c>, que la lee <c>FOR SHARE</c> bajo el lock de membresía— es el lock de la propia
    /// fila: toda escritura de la fila choca con ese <c>FOR SHARE</c>.</para>
    ///
    /// <para>La unicidad del nombre la sostiene <c>ux_familias_nombre</c> (<c>23505</c> → <c>409
    /// familia_nombre_duplicado</c> en <c>ManejadorDeErrores</c>); el chequeo previo es un servicio de UX, no la
    /// garantía: dos ediciones concurrentes al mismo nombre pasan las dos el chequeo y la perdedora recibe el mismo
    /// <c>409</c> del respaldo. Devuelve la familia como la lista (<see cref="FamiliaListado"/>), con la cantidad de
    /// miembros vivos leída después del commit.</para>
    /// </summary>
    public async Task<FamiliaListado> ActualizarAsync(int id, EdicionFamilia datos, CancellationToken ct = default)
    {
        var nombre = NormalizarNombre(datos.Nombre);
        var activo = datos.Activo
            ?? throw new ErrorDominio("activo_requerido", "El campo activo es obligatorio.", 400);

        await ExigirNombreDisponibleAsync(nombre, excluirId: id, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        // Antes de la primera lectura: lo que esta operación deje rastreado se suelta si falla.
        var yaRastreadas = RastreoDeEntidades.Instantanea(db);

        try
        {
            await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await db.Database.BeginTransactionAsync(ct);

                await guarda.BloquearFilaAsync<Familia>(id, ct);

                // La ÚNICA lectura de la familia, nacida bajo el lock: el filtro de baja lógica deja afuera a una familia
                // dada de baja mientras se esperaba el lock.
                var familia = await db.Familias.FirstOrDefaultAsync(f => f.Id == id, ct)
                    ?? throw ErrorDominio.NoEncontrado($"No existe la familia {id}.");

                familia.Nombre = nombre;
                familia.Activo = activo;
                familia.UpdatedAt = reloj.Ahora;

                await db.SaveChangesAsync(ct);
                await transaccion.CommitAsync(ct);
            });
        }
        catch
        {
            RastreoDeEntidades.SoltarLoAgregadoDesde(db, yaRastreadas);
            throw;
        }

        return new FamiliaListado(id, nombre, activo, await db.Articulos.CountAsync(a => a.IdFamilia == id, ct));
    }

    /// <summary>
    /// Saca a un artículo de su familia: queda sin familia (<c>id_familia = NULL</c>) y conserva todos sus valores —los
    /// campos compartidos, los propios y sus precios—, que son los de la familia en el momento de salir. Los demás
    /// miembros no cambian. <c>404</c> si la familia no existe o está dada de baja, o si el artículo no existe o está
    /// dado de baja (también los de otro tenant); <c>409 familia_cambio</c> si el artículo existe pero no es miembro de
    /// esa familia —el cliente tiene que recargar—.
    ///
    /// <para>Salir cambia la pertenencia: el lock de membresía EXCLUSIVO es la primera sentencia de la transacción y
    /// todo lo demás ocurre bajo él. Comprobada la existencia de la familia y del artículo, la fila del artículo se
    /// bloquea <c>FOR NO KEY UPDATE</c> guardada por la familia pedida y por la baja lógica (paso (2) del protocolo,
    /// <see cref="MembresiaDeFamilias.BloquearFilaDelArticuloAsync"/>): si el artículo no es miembro de esa familia, o
    /// otra escritura lo cambió o lo dio de baja mientras se esperaba el lock, es <c>familia_cambio</c> y no se escribe
    /// nada. Recién entonces se escribe, con un solo "ahora". Sin reintento, y es todo o nada: una sola
    /// transacción.</para>
    /// </summary>
    public async Task SacarArticuloAsync(int idFamilia, int idArticulo, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

            await LockDeMembresiaDeFamilias.TomarExclusivoAsync(conexion, transaccionCruda, idTenant, ct);

            if (!await db.Familias.AnyAsync(f => f.Id == idFamilia, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe la familia {idFamilia}.");
            }

            // Solo para el 404 de un artículo que no existe o está dado de baja: el valor que devuelve no se usa. La
            // pertenencia no se decide acá sino en el bloqueo de abajo, que la comprueba contra la familia pedida bajo
            // el lock de la fila.
            await MembresiaDeFamilias.LeerIdFamiliaAsync(conexion, transaccionCruda, idArticulo, idTenant, ct);

            await MembresiaDeFamilias.BloquearFilaDelArticuloAsync(
                conexion, transaccionCruda, idArticulo, idFamilia, idTenant, ct);

            await DesvincularArticulosAsync(conexion, transaccionCruda, [idArticulo], idTenant, reloj.Ahora, ct);

            await transaccion.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Disuelve la familia: todos sus miembros vivos quedan sin familia (<c>id_familia = NULL</c>, con todos sus
    /// valores) y la familia se da de baja, en una sola transacción y con un solo "ahora". Los artículos dados de baja
    /// no cuentan como miembros y conservan su <c>id_familia</c>, igual que antes. <c>404</c> si la familia no existe o
    /// ya está dada de baja (también la de otro tenant). El nombre de una familia disuelta se puede reutilizar.
    ///
    /// <para>Cambia la pertenencia de todos los miembros, así que toma, en el orden global de los locks de las
    /// familias, el lock de membresía EXCLUSIVO como primera sentencia, la fila de la familia (<c>FOR UPDATE</c>, antes
    /// de leerla: la familia se lee UNA sola vez, bajo ese lock) y las filas de los miembros vivos ascendentes por id
    /// (<see cref="MembresiaDeFamilias.BloquearMiembrosAsync"/>, que reevalúa la baja lógica bajo el lock de cada
    /// fila). La escritura alcanza solo a las filas bloqueadas. Sin reintento (<c>ef-retry-safe-writes</c>, forma
    /// (b): un reintento tras un commit ambiguo no encontraría la familia ya dada de baja y respondería <c>404</c> a
    /// una disolución que sí tuvo éxito); lo que la operación dejó rastreado se suelta si falla.</para>
    /// </summary>
    public async Task DisolverAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        // Antes de la primera lectura: lo que esta operación deje rastreado se suelta si falla.
        var yaRastreadas = RastreoDeEntidades.Instantanea(db);

        try
        {
            await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await db.Database.BeginTransactionAsync(ct);

                var conexion = await ObtenerConexionAbiertaAsync(ct);
                var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

                await LockDeMembresiaDeFamilias.TomarExclusivoAsync(conexion, transaccionCruda, idTenant, ct);

                await guarda.BloquearFilaAsync<Familia>(id, ct);

                // La ÚNICA lectura de la familia, nacida bajo los dos locks: el filtro de baja lógica deja afuera a una
                // familia que otra disolución dio de baja mientras se esperaba.
                var familia = await db.Familias.FirstOrDefaultAsync(f => f.Id == id, ct)
                    ?? throw ErrorDominio.NoEncontrado($"No existe la familia {id}.");

                var miembros = await MembresiaDeFamilias.BloquearMiembrosAsync(conexion, transaccionCruda, id, idTenant, ct);

                var ahora = reloj.Ahora;

                await DesvincularArticulosAsync(conexion, transaccionCruda, miembros, idTenant, ahora, ct);

                familia.DeletedAt = ahora;
                familia.UpdatedAt = ahora;

                await db.SaveChangesAsync(ct);
                await transaccion.CommitAsync(ct);
            });
        }
        catch
        {
            RastreoDeEntidades.SoltarLoAgregadoDesde(db, yaRastreadas);
            throw;
        }
    }

    /// <summary>Deja sin familia a los artículos <paramref name="idsBloqueados"/> —sus filas ya bloqueadas por quien
    /// llama—, con <paramref name="ahora"/> como <c>updated_at</c>. <c>UPDATE</c> crudo por clave sobre exactamente
    /// esas filas: no mira la familia ni la baja lógica, que el bloqueo ya verificó. Con una lista vacía no emite
    /// ninguna sentencia.</summary>
    private static async Task DesvincularArticulosAsync(
        DbConnection conexion, DbTransaction? transaccion, IReadOnlyList<int> idsBloqueados, int idTenant,
        DateTimeOffset ahora, CancellationToken ct)
    {
        if (idsBloqueados.Count == 0)
        {
            return;
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE articulos SET id_familia = NULL, updated_at = $1 WHERE id_articulo = ANY($2) AND id_tenant = $3";

        ParametrosDeComando.Agregar(comando, ahora);
        ParametrosDeComando.Agregar(comando, idsBloqueados.ToArray());
        ParametrosDeComando.Agregar(comando, idTenant);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant: un actor de plataforma nunca llega hasta
            // acá. Defensa en profundidad, no un camino alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeFamilias requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    /// <summary>Pre-chequeo best-effort del nombre (<c>db-error-backstops</c>): el contrato real es
    /// <c>ux_familias_nombre</c> con su traducción a <c>409 familia_nombre_duplicado</c>. La comparación es la de la
    /// columna <c>citext</c>, sin distinguir mayúsculas. <paramref name="excluirId"/> es la familia que se edita: su
    /// propio nombre no es un duplicado.</summary>
    private async Task ExigirNombreDisponibleAsync(string nombre, int? excluirId, CancellationToken ct)
    {
        if (await db.Familias.AnyAsync(f => f.Nombre == nombre && f.Id != excluirId, ct))
        {
            throw ErrorDominio.Conflicto(
                "familia_nombre_duplicado", $"Ya existe una familia llamada \"{nombre}\" en este tenant.");
        }
    }

    private static string NormalizarNombre(string? valor)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio("nombre_requerido", "El campo nombre es obligatorio.", 400);
        }

        if (limpio.Length > LargoMaximoDelNombre)
        {
            throw new ErrorDominio(
                "nombre_muy_largo", $"El campo nombre no puede superar los {LargoMaximoDelNombre} caracteres.", 400);
        }

        return limpio;
    }

    private async Task<IReadOnlyList<EstadoDePreciosDeLista>> EstadoDePreciosDeLaReferenciaAsync(
        int idArticuloReferencia, CancellationToken ct)
    {
        var ahora = reloj.Ahora;

        var listas = await db.ListasPrecio
            .Where(l => l.Modo == ModoLista.Fija)
            .OrderBy(l => l.Id)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var filas = await db.Precios
            .AsNoTracking()
            .Where(p => p.IdArticulo == idArticuloReferencia)
            .Select(p => new { p.IdListaPrecio, p.Monto, p.VigenteDesde, p.VigenteHasta })
            .ToListAsync(ct);

        var filasPorLista = filas.ToLookup(f => f.IdListaPrecio);

        return
        [
            .. listas.Select(idLista => new EstadoDePreciosDeLista(
                idLista,
                EstadoDePrecios.De(
                    [.. filasPorLista[idLista].Select(f => new FilaDePrecio(f.Monto, f.VigenteDesde, f.VigenteHasta))],
                    ahora)))
        ];
    }
}
