using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Common;

namespace Ways.Application.Bajas;

/// <summary>
/// El guard de uso de los catálogos de tenant (áreas, categorías, marcas, grupos, medios de pago,
/// listas de precio) y de proveedores: la política que el dueño del producto aprobó es que una
/// baja SOLO procede si NINGUNA fila referencia la entidad, sin importar si esa fila fue creada
/// antes o después que la entidad, y sin importar si esa fila referenciante ya está dada de baja
/// lógicamente (OD4 se mantiene: <see cref="InspectorDeUso"/> no emite <c>deleted_at</c> en
/// ninguna rama, ni siquiera en modo referencia).
///
/// POR QUÉ NO ES <see cref="InspectorDeUso.PrimeraDependenciaEnUsoAsync"/>: ese método (modo
/// organización, stage-20) corta cada rama <c>Marcado</c> con <c>created_at &gt; ancla</c> para que
/// lo que el aprovisionamiento creó en el mismo instante no bloquee. Esa comparación asume que la
/// FK es INMUTABLE una vez escrita (un punto de venta no cambia de empresa). Las FKs de catálogo
/// son MUTABLES: un artículo creado en enero se puede reasignar a una marca creada en febrero, y
/// ahí <c>articulo.created_at &lt; marca.created_at</c> — la comparación de ancla NUNCA vería esa
/// reasignación y la marca se podría borrar con una FK colgando. <see cref="InspectorDeUso.TablasQueReferencianAsync"/>
/// (modo referencia) es existencia pura, sin ningún corte temporal, exactamente por esto.
///
/// <see cref="BloquearFilaAsync{T}"/> toma <c>FOR UPDATE</c> sobre la fila ANTES de leerla y ANTES
/// de preguntarle al inspector: el chequeo de FK de un escritor concurrente que está insertando o
/// actualizando una fila referenciante toma <c>FOR KEY SHARE</c> sobre la fila referenciada — ese
/// modo NO choca contra el <c>FOR NO KEY UPDATE</c> que usa un <c>UPDATE ... SET deleted_at</c>
/// corriente, pero SÍ choca contra <c>FOR UPDATE</c>. Con el lock tomado primero, un escritor que ya
/// insertó (sin comitear todavía) una fila referenciante hace esperar a esta baja hasta que
/// comitee, y el inspector —que corre DESPUÉS del lock— ve esa fila ya comiteada y responde 409.
/// Sin el lock, las dos transacciones podrían entrelazarse de forma que ninguna de las dos viera a
/// la otra y las dos comitearan: un artículo referenciando una marca borrada.
///
/// RESIDUAL CONOCIDO: un escritor cuyo pre-chequeo de existencia (filtrado, sin lock) corrió
/// ANTES del commit de esta baja, y cuyo INSERT/UPDATE llega DESPUÉS de que esta baja tomó el
/// lock, puede comitear una referencia a la fila recién dada de baja: su chequeo de FK espera el
/// lock y después pasa, porque la fila sigue existiendo físicamente. Se cierra del lado del
/// escritor, con un <c>SELECT ... WHERE deleted_at IS NULL FOR KEY SHARE</c> dentro de su
/// transacción en lugar del pre-chequeo — <see cref="BloquearSiEstaVivaAsync{T}"/>.
///
/// fix/articulos-lock-referencias: el residual queda CERRADO para los escritores de artículos
/// (área/categoría/marca/grupo/proveedor habitual, los únicos campos de <c>articulos</c> que
/// referencian estos 5 catálogos — <c>ServicioDeArticulos.CrearAsync</c>/<c>ActualizarAsync</c>
/// reemplazan su pre-chequeo <c>AnyAsync</c> filtrado por <see cref="BloquearSiEstaVivaAsync{T}"/>
/// dentro de su propia transacción). Sigue ABIERTO para el resto de los escritores (ventas,
/// gastos, compras, órdenes de compra, ofertas, clientes, precios): cada uno conserva un
/// pre-chequeo filtrado sin lock.
/// </summary>
public sealed class GuardaDeReferencias(IWaysDbContext db, InspectorDeUso inspector)
{
    /// <summary>
    /// <c>SELECT ... FOR UPDATE</c> crudo sobre la fila por su PK, usando identificadores que
    /// salen únicamente de la metadata de EF (mismo criterio de superficie cerrada que
    /// <see cref="InspectorDeUso"/> — de hecho, la MISMA validación:
    /// <see cref="InspectorDeUso.Identificador"/>). El resultado se descarta: 0 filas significa
    /// "no existe" o "es de otro tenant" (RLS ya la esconde), y la relectura de EF que sigue da el
    /// 404 correcto en cualquiera de los dos casos.
    /// </summary>
    public async Task BloquearFilaAsync<T>(int id, CancellationToken ct = default)
        where T : class
    {
        var metadata = ResolverMetadataDeTabla<T>();

        // Un FOR UPDATE fuera de una transacción se libera apenas termina el statement: el lock
        // tiene que sobrevivir hasta el COMMIT/ROLLBACK del llamador para servir de algo.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "BloquearFilaAsync necesita una transacción abierta: un FOR UPDATE fuera de una " +
                "transacción se libera apenas termina el statement.");
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        comando.CommandText =
            $"SELECT 1 FROM \"{metadata.Esquema}\".\"{metadata.Tabla}\" " +
            $"WHERE \"{metadata.ColumnaPk}\" = $1 FOR UPDATE";

        ParametrosDeComando.Agregar(comando, id);

        await comando.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// Cierra el RESIDUAL documentado en la clase, del lado del escritor: en vez de un
    /// pre-chequeo filtrado sin lock (<c>AnyAsync</c> bajo el query filter <c>BajaLogica</c>),
    /// toma <c>FOR KEY SHARE</c> sobre la fila VIVA (<c>deleted_at IS NULL</c>) ANTES del
    /// INSERT/UPDATE que la referencia, dentro de la misma transacción.
    ///
    /// Por qué <c>FOR KEY SHARE</c> y no <c>FOR UPDATE</c> (a diferencia de
    /// <see cref="BloquearFilaAsync{T}"/>): es el lock MÁS DÉBIL que choca contra el
    /// <c>FOR UPDATE</c> que toma la baja — no bloquea renombres ni ningún otro escritor que no
    /// toque la clave. Bajo READ COMMITTED, después de esperar el lock, Postgres REEVALÚA
    /// <c>deleted_at IS NULL</c> sobre la versión ya comiteada: si la baja ganó la carrera, la
    /// relectura da 0 filas y este método devuelve <c>false</c> — el llamador rechaza la
    /// escritura en vez de comitear una referencia colgante.
    /// </summary>
    public async Task<bool> BloquearSiEstaVivaAsync<T>(int id, CancellationToken ct = default)
        where T : class
    {
        var metadata = ResolverMetadataDeTabla<T>();

        var propiedadDeletedAt = metadata.Entidad.FindProperty(nameof(EntidadBase.DeletedAt))
            ?? throw new InvalidOperationException(
                $"El tipo {typeof(T).FullName} no tiene la propiedad {nameof(EntidadBase.DeletedAt)}: " +
                "BloquearSiEstaVivaAsync solo sabe bloquear entidades con baja lógica.");

        var columnaDeletedAt = propiedadDeletedAt.GetColumnName(metadata.Objeto)
            ?? throw new InvalidOperationException(
                $"La columna {nameof(EntidadBase.DeletedAt)} de {typeof(T).FullName} no resuelve " +
                $"columna en {metadata.Tabla}.");

        var columnaDeletedAtValidada = InspectorDeUso.Identificador(columnaDeletedAt);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "BloquearSiEstaVivaAsync necesita una transacción abierta: un FOR KEY SHARE fuera " +
                "de una transacción se libera apenas termina el statement.");
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        comando.CommandText =
            $"SELECT 1 FROM \"{metadata.Esquema}\".\"{metadata.Tabla}\" " +
            $"WHERE \"{metadata.ColumnaPk}\" = $1 AND \"{columnaDeletedAtValidada}\" IS NULL " +
            "FOR KEY SHARE";

        ParametrosDeComando.Agregar(comando, id);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    /// <summary>Resolución de esquema/tabla/columna de PK compartida por
    /// <see cref="BloquearFilaAsync{T}"/> y <see cref="BloquearSiEstaVivaAsync{T}"/> — mismos
    /// identificadores, misma validación (<see cref="InspectorDeUso.Identificador"/>), un solo
    /// lugar que los arma a partir de la metadata de EF.</summary>
    private (IEntityType Entidad, StoreObjectIdentifier Objeto, string Esquema, string Tabla, string ColumnaPk)
        ResolverMetadataDeTabla<T>()
        where T : class
    {
        var tipo = typeof(T);

        var entidad = db.Model.FindEntityType(tipo)
            ?? throw new InvalidOperationException(
                $"El tipo {tipo.FullName} no está mapeado en el modelo: GuardaDeReferencias no " +
                "puede bloquear su fila.");

        var tabla = entidad.GetTableName()
            ?? throw new InvalidOperationException(
                $"El tipo {tipo.FullName} no tiene tabla mapeada: GuardaDeReferencias no puede " +
                "bloquear su fila.");

        var esquema = entidad.GetSchema() ?? db.Model.GetDefaultSchema() ?? "public";

        var clavePrincipal = entidad.FindPrimaryKey()
            ?? throw new InvalidOperationException(
                $"El tipo {tipo.FullName} no tiene clave primaria: GuardaDeReferencias no puede " +
                "bloquear su fila.");

        if (clavePrincipal.Properties.Count != 1)
        {
            throw new InvalidOperationException(
                $"El tipo {tipo.FullName} tiene una clave primaria compuesta " +
                $"({string.Join(", ", clavePrincipal.Properties.Select(p => p.Name))}): " +
                "GuardaDeReferencias solo sabe bloquear filas de clave simple.");
        }

        var objeto = StoreObjectIdentifier.Create(entidad, StoreObjectType.Table)
            ?? throw new InvalidOperationException(
                $"El tipo {tipo.FullName} no resuelve a un objeto de almacenamiento de tipo tabla.");

        var columnaPk = clavePrincipal.Properties[0].GetColumnName(objeto)
            ?? throw new InvalidOperationException(
                $"La clave primaria de {tipo.FullName} no resuelve columna en {tabla}.");

        return (
            entidad, objeto,
            InspectorDeUso.Identificador(esquema),
            InspectorDeUso.Identificador(tabla),
            InspectorDeUso.Identificador(columnaPk));
    }

    /// <summary>
    /// Lee los valores de ancla de <paramref name="entidad"/> por reflexión, en el orden que
    /// <see cref="InventarioDeDependientes.PropiedadesDeAncla"/> exige, y pregunta al inspector en
    /// modo referencia. Si alguna rama disparó, levanta <c>409</c> con <paramref name="codigo"/> y
    /// el mensaje que compone <see cref="ComponerMensaje"/>.
    /// </summary>
    public async Task ExigirSinReferenciasAsync<T>(
        T entidad,
        string codigo,
        string sujeto,
        IReadOnlyDictionary<string, string>? etiquetasPropias,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entidad);

        var tipoAncla = typeof(T);
        var propiedades = InventarioDeDependientes.PropiedadesDeAncla(db.Model, tipoAncla);

        var valoresDeClave = propiedades
            .Select(propiedad => LeerValorDeAncla(entidad, propiedad, tipoAncla))
            .ToList();

        var etiquetas = await inspector.TablasQueReferencianAsync(tipoAncla, valoresDeClave, ct);

        if (etiquetas.Count == 0)
        {
            return;
        }

        throw ErrorDominio.Conflicto(codigo, ComponerMensaje(sujeto, etiquetas, etiquetasPropias));
    }

    /// <summary>
    /// Pura y testeable sin base: compone "No se puede dar de baja {sujeto} porque tiene
    /// {a, b y c}." — cada etiqueta de rama se traduce con <paramref name="etiquetasPropias"/> si
    /// el catálogo declaró una propia para esa tabla, o si no con
    /// <see cref="EtiquetasDeTablas.DescribirBloqueo"/> (que además nombra el puente cuando la
    /// rama llegó por uno). Distinto y en el orden que <see cref="InspectorDeUso.TablasQueReferencianAsync"/>
    /// ya entrega (el orden del inventario).
    /// </summary>
    public static string ComponerMensaje(
        string sujeto,
        IReadOnlyList<string> etiquetasDeRama,
        IReadOnlyDictionary<string, string>? etiquetasPropias)
    {
        ArgumentNullException.ThrowIfNull(sujeto);
        ArgumentNullException.ThrowIfNull(etiquetasDeRama);

        var descripciones = etiquetasDeRama
            .Select(etiqueta => etiquetasPropias is not null
                && etiquetasPropias.TryGetValue(etiqueta, out var propia)
                    ? propia
                    : EtiquetasDeTablas.DescribirBloqueo(etiqueta))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return $"No se puede dar de baja {sujeto} porque tiene {UnirConY(descripciones)}.";
    }

    /// <summary>Join en castellano: "a" / "a y b" / "a, b y c" — nunca una coma antes del "y"
    /// final.</summary>
    private static string UnirConY(IReadOnlyList<string> partes) => partes.Count switch
    {
        0 => string.Empty,
        1 => partes[0],
        _ => string.Join(", ", partes.Take(partes.Count - 1)) + " y " + partes[^1],
    };

    private static object LeerValorDeAncla(object entidad, string propiedad, Type tipoAncla)
    {
        var propiedadClr = tipoAncla.GetProperty(propiedad)
            ?? throw new InvalidOperationException(
                $"El tipo {tipoAncla.FullName} no tiene la propiedad {propiedad}, que alguna rama " +
                "del inspector necesita leer del ancla.");

        return propiedadClr.GetValue(entidad)
            ?? throw new InvalidOperationException(
                $"La propiedad {propiedad} de {tipoAncla.FullName} es null: el guard de " +
                "referencias no liga valores de clave nulos.");
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }
}
