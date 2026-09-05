using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Ways.Domain.Clientes;
using Ways.Domain.Common;

namespace Ways.Application.Organizacion;

/// <summary>
/// Cómo se interroga a una tabla dependiente para saber si el cliente operó ahí
/// (stage-20 design D3, sección A). Tres baldes, evaluados en orden fijo sobre el TIPO de
/// entidad dependiente — nunca sobre la columna que apunta al ancla, porque el discriminante
/// (¿la tabla lleva <c>created_at</c>?) es una propiedad de la tabla.
/// </summary>
public enum ClasificacionDeDependiente
{
    /// <summary>Carve-out: no emite rama SQL. Exactamente dos, cada uno con su razón escrita
    /// en <see cref="InventarioDeDependientes.Excluidos"/>.</summary>
    Excluido,

    /// <summary>La tabla lleva <c>created_at</c>: la rama pregunta por filas POSTERIORES al
    /// instante del ancla (<c>&gt;</c> estricto), así que lo que creó el aprovisionamiento —
    /// que comparte el instante del ancla — no bloquea.</summary>
    Marcado,

    /// <summary>La tabla no lleva <c>created_at</c>: la rama pregunta solo por existencia.
    /// El aprovisionamiento no crea ninguna fila sin marca salvo el contador excluido, así
    /// que existir ya significa uso — y falla del lado seguro (sobre-bloquea).</summary>
    SinMarca,
}

/// <summary>
/// La tabla ESTRUCTURAL por la que una rama llega al ancla cuando la tabla hoja no la referencia
/// directamente (stage-20 design D3). El uso sube por la jerarquía: una venta se cuelga del punto
/// de venta, y el punto de venta se cuelga de la empresa, así que la empresa está EN USO.
/// </summary>
/// <param name="Esquema">Esquema de la tabla puente. Sale de la metadata de EF.</param>
/// <param name="Tabla">Nombre pelado de la tabla puente (hoy, <c>puntos_venta</c>).</param>
/// <param name="ColumnasDeUnion">Columnas del PUENTE contra las que se unen las de la hoja,
/// zipeadas 1 a 1 con <see cref="RamaDeUso.Columnas"/>.</param>
/// <param name="ColumnasHaciaElAncla">Columnas del PUENTE que apuntan al ancla, zipeadas 1 a 1
/// con <see cref="RamaDeUso.PropiedadesDelPrincipal"/>. Son las que llevan los parámetros
/// posicionales.</param>
public sealed record PuenteDeUso(
    string Esquema,
    string Tabla,
    IReadOnlyList<string> ColumnasDeUnion,
    IReadOnlyList<string> ColumnasHaciaElAncla);

/// <summary>
/// Una rama del inspector: la tabla dependiente, las columnas de su FK zipeadas con las
/// propiedades del principal que las alimentan, y el balde que decide su predicado.
/// </summary>
/// <param name="Esquema">Esquema de la tabla dependiente. Sale de la metadata de EF; el
/// renderizador lo necesita porque el walk es puro (D2) y no le pasa el <see cref="IModel"/>.</param>
/// <param name="Tabla">Nombre pelado de la tabla. Es también la etiqueta que
/// <c>InspectorDeUso</c> devuelve al llamador: siempre la tabla HOJA, la que el operador necesita
/// ver, incluso cuando se llega a ella por un puente.</param>
/// <param name="Columnas">Columnas dependientes de la FK, en el orden de
/// <c>fk.Properties</c>. Con puente son las columnas de la hoja hacia el PUENTE.</param>
/// <param name="PropiedadesDelPrincipal">Propiedades del principal, zipeadas 1 a 1 con
/// <paramref name="Columnas"/>. Una FK compuesta <c>(id, id_tenant)</c> o de clave alternativa
/// sale de acá sin ningún caso especial. Con puente son las propiedades del ANCLA, zipeadas con
/// <see cref="PuenteDeUso.ColumnasHaciaElAncla"/>.</param>
/// <param name="Puente">Tabla estructural intermedia, o <c>null</c> si la hoja referencia al ancla
/// directamente.</param>
public sealed record RamaDeUso(
    string Esquema,
    string Tabla,
    IReadOnlyList<string> Columnas,
    IReadOnlyList<string> PropiedadesDelPrincipal,
    ClasificacionDeDependiente Clasificacion,
    PuenteDeUso? Puente = null)
{
    public bool UsaAncla => Clasificacion is ClasificacionDeDependiente.Marcado;

    /// <summary>
    /// Etiqueta estable de la rama para el golden N3 y para los mensajes de error: la hoja, y el
    /// puente explícito cuando lo hay. Nunca es lo que se emite al statement.
    /// </summary>
    public string Etiqueta => Puente is null ? Tabla : $"{Tabla} via {Puente.Tabla}";
}

/// <summary>
/// Recorre la metadata de EF y arma el inventario de todo lo que apunta a una entidad de
/// organización (stage-20 design D2, D3).
///
/// PURO a propósito: sin base, sin reloj, sin DI. Es lo que hace posible la red N3 (el golden
/// del inventario) — un golden sobre una función que necesita una base viva es un golden que
/// nadie regenera — y lo que permite correr N1/N2/N3 sin contenedor, sobre el modelo Npgsql
/// real construido sobre una conexión que nunca se abre
/// (<c>tests/Ways.Application.Tests/Persistencia/Modelo*Tests.cs</c>).
///
/// El conjunto de dependientes es la UNIÓN de tres recorridos, los tres derivados del modelo y
/// ninguno escrito a mano:
///
/// <list type="number">
/// <item><see cref="IEntityType.GetReferencingForeignKeys"/> — lo que apunta al ancla
/// directamente.</item>
/// <item>Solo para el ancla <see cref="Ways.Domain.Organizacion.Tenant"/>: toda entidad que hereda
/// de <see cref="EntidadTenant"/> y está mapeada a la columna de alcance <c>id_tenant</c>, el mismo
/// idioma por reflexión que ya usa <c>WaysDbContext.AplicarFiltroDeTenant</c> para el query
/// filter.</item>
/// <item>Solo para el ancla <see cref="Ways.Domain.Organizacion.Empresa"/>: el inventario
/// ejecutable del ancla <see cref="Ways.Domain.Organizacion.PuntoVenta"/>, PUENTEADO por
/// <c>puntos_venta</c>. El uso sube por la jerarquía estructural, y ninguna tabla operativa lleva
/// <c>id_empresa</c>: ventas, pagos, stock, caja y tesorería se cuelgan todas del punto de
/// venta.</item>
/// </list>
///
/// Una tabla que una etapa futura agregue entra sola por cualquiera de los tres caminos.
/// La clasificación es TOTAL por construcción — ningún <c>else</c> puede tirar en runtime — y las
/// imposibilidades MECÁNICAS que sí tiran (<see cref="InvalidOperationException"/> nombrando el
/// tipo CLR y el origen de la rama) son fallas de build-time que ejecuta N1 en CI, nunca un 500
/// en producción sobre un intento de baja.
/// </summary>
public static class InventarioDeDependientes
{
    /// <summary>
    /// Columna que define el balde <see cref="ClasificacionDeDependiente.Marcado"/> y contra la
    /// que se compara el instante del ancla. Es una constante y no una convención implícita
    /// porque N2 la vuelve a resolver por su cuenta desde el modelo.
    /// </summary>
    public const string ColumnaDeMarcaTemporal = "created_at";

    /// <summary>
    /// Columna de alcance de tenant, y la razón por la que el ancla
    /// <see cref="Ways.Domain.Organizacion.Tenant"/> necesita una SEGUNDA fuente además del
    /// recorrido de FKs: una tabla scopeada por tenant puede no declarar ninguna FK contra
    /// <c>tenants</c> porque su FK compuesta apunta al padre intermedio y arrastra el
    /// <c>id_tenant</c> ahí — <c>puntos_venta</c> declara <c>(id_empresa, id_tenant) → empresas</c>
    /// y nada más. El alcance por columna es lo que mantiene esa tabla DENTRO del inventario, para
    /// que un tenant cuyo cliente agregó un segundo punto de venta no lea PRÍSTINO: la falla
    /// ABIERTA es la única dirección que esta etapa no acepta.
    /// </summary>
    public const string ColumnaDeAlcanceDeTenant = "id_tenant";

    /// <summary>
    /// Carve-outs. EXACTAMENTE dos, cada uno con su razón escrita (design sección A, balde 1):
    ///
    /// <list type="bullet">
    /// <item><see cref="Ways.Domain.Auditoria.Auditoria"/> — el rastro de auditoría es un
    /// registro ACERCA de la entidad, no algo que el cliente "operó" ahí. La baja es lógica, así
    /// que la fila referenciada sobrevive y el rastro se sigue renderizando; si auditoría
    /// bloqueara, la primera acción registrada sobre una entidad la volvería indeleteable para
    /// siempre.</item>
    /// <item><see cref="NumeracionCliente"/> — el contador de aprovisionamiento. Lo inserta con
    /// SQL crudo <c>AsignadorDeNumeroCliente.AsegurarContadorAsync</c> al crear el tenant, no
    /// hereda de <see cref="EntidadBase"/> (no tiene <c>created_at</c>, así que caería en
    /// <see cref="ClasificacionDeDependiente.SinMarca"/> y bloquearía a TODO tenant recién
    /// aprovisionado) y no es dato cargado por el cliente.</item>
    /// </list>
    ///
    /// Un carve-out no emite ninguna rama SQL: aparece en <see cref="InventarioCompleto"/> —
    /// para que el golden N3 también fije este conjunto — y no aparece en
    /// <see cref="Construir"/>.
    /// </summary>
    public static readonly FrozenSet<Type> Excluidos = FrozenSet.ToFrozenSet(
    [
        typeof(Ways.Domain.Auditoria.Auditoria),
        typeof(NumeracionCliente),
    ]);

    /// <summary>
    /// Las ramas EJECUTABLES del ancla: el inventario completo menos los carve-outs.
    /// Es lo que <c>InspectorDeUso</c> renderiza.
    /// </summary>
    public static IReadOnlyList<RamaDeUso> Construir(IModel modelo, Type tipoAncla) =>
        [.. InventarioCompleto(modelo, tipoAncla)
            .Where(rama => rama.Clasificacion is not ClasificacionDeDependiente.Excluido)];

    /// <summary>
    /// TODO dependiente del ancla, carve-outs incluidos, en orden determinístico
    /// (tabla, luego columnas). Es la UNIÓN del recorrido de FKs con las ramas de alcance de
    /// tenant, deduplicada por (tabla, columnas). Es la fuente del golden N3: un carve-out que se
    /// agregue o se saque cambia una línea del archivo en vez de desaparecer sin dejar rastro.
    /// </summary>
    public static IReadOnlyList<RamaDeUso> InventarioCompleto(IModel modelo, Type tipoAncla)
    {
        ArgumentNullException.ThrowIfNull(modelo);
        ArgumentNullException.ThrowIfNull(tipoAncla);

        var ancla = modelo.FindEntityType(tipoAncla)
            ?? throw new InvalidOperationException(
                $"El tipo ancla {tipoAncla.FullName} no está mapeado en el modelo.");

        var ramas = ancla.GetReferencingForeignKeys()
            .Select(fk => ConstruirRama(fk, tipoAncla))
            .ToList();

        AgregarRamasDeAlcanceDeTenant(ancla, tipoAncla, ramas);
        AgregarRamasPuenteadasPorPuntoDeVenta(ancla, tipoAncla, ramas);

        ramas.Sort(static (a, b) =>
        {
            var porTabla = string.CompareOrdinal(a.Tabla, b.Tabla);

            if (porTabla != 0)
            {
                return porTabla;
            }

            var porColumnas = string.CompareOrdinal(
                string.Join(',', a.Columnas), string.Join(',', b.Columnas));

            return porColumnas != 0
                ? porColumnas
                : string.CompareOrdinal(a.Puente?.Tabla ?? string.Empty, b.Puente?.Tabla ?? string.Empty);
        });

        return ramas;
    }

    /// <summary>
    /// Las propiedades del ancla que alguna rama necesita leer, distintas y ordenadas.
    /// Define el contrato posicional de <c>valoresDeClave</c>: <c>valoresDeClave[i]</c> es el
    /// valor de <c>PropiedadesDeAncla(...)[i]</c>, y ese mismo índice es el número de parámetro
    /// <c>$(i+1)</c> del statement. Sin esta lista el llamador no tendría forma de saber en qué
    /// orden pasar los valores de una clave compuesta.
    /// </summary>
    public static IReadOnlyList<string> PropiedadesDeAncla(IModel modelo, Type tipoAncla) =>
        [.. InventarioCompleto(modelo, tipoAncla)
            .Where(rama => rama.Clasificacion is not ClasificacionDeDependiente.Excluido)
            .SelectMany(rama => rama.PropiedadesDelPrincipal)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// La segunda fuente del conjunto de dependientes, y CIERRA UNA CLASE, no un caso: toda
    /// entidad que hereda de <see cref="EntidadTenant"/> está scopeada por tenant por declaración
    /// (es de lo que se cuelgan el query filter, el estampado de <c>SaveChanges</c> y RLS), tenga
    /// o no una FK declarada contra <c>tenants</c>. Se recorre por reflexión sobre el modelo —el
    /// mismo idioma de <c>WaysDbContext.AplicarFiltroDeTenant</c>— y se deduplica contra lo que ya
    /// aportó el recorrido de FKs, así que una tabla cubierta por ambos caminos emite UNA rama.
    /// La clasificación usa exactamente la misma regla de baldes.
    /// </summary>
    private static void AgregarRamasDeAlcanceDeTenant(
        IEntityType ancla, Type tipoAncla, List<RamaDeUso> ramas)
    {
        if (tipoAncla != typeof(Ways.Domain.Organizacion.Tenant))
        {
            return;
        }

        var clavePrincipal = ancla.FindPrimaryKey()
            ?? throw new InvalidOperationException(
                $"El tipo ancla {tipoAncla.FullName} no tiene clave primaria: el inventario no " +
                "puede sintetizar sus ramas de alcance de tenant.");

        // La rama sintetizada zipea UNA columna (id_tenant) contra la clave principal del ancla.
        // Con una clave compuesta el zip trunca en silencio y la rama quedaría ligando solo la
        // primera propiedad: sub-bloqueo silencioso. Es una imposibilidad mecánica, así que se
        // tira nombrando la clave en vez de emitir una rama incompleta.
        if (clavePrincipal.Properties.Count != 1)
        {
            throw new InvalidOperationException(
                $"El ancla {tipoAncla.FullName} tiene una clave primaria de " +
                $"{clavePrincipal.Properties.Count} propiedades " +
                $"({string.Join(", ", clavePrincipal.Properties.Select(p => p.Name))}) y la rama de " +
                $"alcance de tenant solo puede zipear la columna {ColumnaDeAlcanceDeTenant}: el " +
                "inventario necesita una columna de alcance por propiedad de la clave.");
        }

        var yaCubiertas = ramas.Select(ClaveDeRama).ToHashSet(StringComparer.Ordinal);

        foreach (var dependiente in ancla.Model.GetEntityTypes()
            .Where(tipo => typeof(EntidadTenant).IsAssignableFrom(tipo.ClrType)))
        {
            var origen = $"{dependiente.ClrType.Name}.{nameof(EntidadTenant.IdTenant)}";

            var tabla = dependiente.GetTableName()
                ?? throw new InvalidOperationException(
                    $"El tipo dependiente {dependiente.ClrType.FullName} hereda de " +
                    $"{nameof(EntidadTenant)} pero no tiene tabla mapeada: el inspector de uso no " +
                    "puede interrogarlo.");

            var objeto = StoreObjectIdentifier.Create(dependiente, StoreObjectType.Table)
                ?? throw new InvalidOperationException(
                    $"El tipo dependiente {dependiente.ClrType.FullName} hereda de " +
                    $"{nameof(EntidadTenant)} pero no resuelve a un objeto de almacenamiento de " +
                    "tipo tabla.");

            if (!dependiente.GetProperties().Any(p => p.GetColumnName(objeto) == ColumnaDeAlcanceDeTenant))
            {
                throw new InvalidOperationException(
                    $"El tipo dependiente {dependiente.ClrType.FullName} hereda de " +
                    $"{nameof(EntidadTenant)} y no resuelve la columna {ColumnaDeAlcanceDeTenant} " +
                    $"en {tabla}.");
            }

            var rama = new RamaDeUso(
                dependiente.GetSchema() ?? EsquemaPorDefecto(dependiente.Model),
                tabla,
                [ColumnaDeAlcanceDeTenant],
                [.. clavePrincipal.Properties.Select(p =>
                    LeerPropiedadDelPrincipal(p, origen, dependiente.ClrType, tipoAncla))],
                Clasificar(dependiente, objeto, origen, tipoAncla));

            if (yaCubiertas.Add(ClaveDeRama(rama)))
            {
                ramas.Add(rama);
            }
        }
    }

    /// <summary>
    /// La TERCERA fuente del conjunto de dependientes, y también cierra una CLASE: el uso propaga
    /// HACIA ARRIBA por la jerarquía estructural. Ninguna tabla operativa lleva <c>id_empresa</c>
    /// —comprobantes, items, pagos, movimientos de stock/caja/tesorería/cuenta corriente, turnos,
    /// presupuestos, remitos, órdenes de compra y gastos se clavan todos en <c>id_punto_venta</c>—,
    /// así que los referenciantes DIRECTOS de una empresa son solo estructura y catálogo. Sin este
    /// recorrido, una empresa con historia operativa completa lee PRÍSTINA: falla ABIERTA, la
    /// dirección de pérdida de datos.
    ///
    /// Cada rama ejecutable del ancla <see cref="Ways.Domain.Organizacion.PuntoVenta"/> se reemite
    /// puenteada por <c>puntos_venta</c>: la hoja se une al puente por las mismas columnas con las
    /// que ya lo referenciaba, y el puente lleva los parámetros del ancla. UNA sola consulta, no N
    /// por punto de venta. La etiqueta devuelta al operador sigue siendo la tabla HOJA.
    ///
    /// El ancla <see cref="Ways.Domain.Organizacion.Tenant"/> NO lo necesita: toda tabla del
    /// modelo lleva <c>id_tenant</c>, y la segunda fuente ya la trae. <c>PuntoVenta</c> y
    /// <c>Usuario</c> son hojas de la jerarquía: no tienen hijos estructurales.
    /// </summary>
    private static void AgregarRamasPuenteadasPorPuntoDeVenta(
        IEntityType ancla, Type tipoAncla, List<RamaDeUso> ramas)
    {
        if (tipoAncla != typeof(Ways.Domain.Organizacion.Empresa))
        {
            return;
        }

        var puente = ancla.Model.FindEntityType(typeof(Ways.Domain.Organizacion.PuntoVenta))
            ?? throw new InvalidOperationException(
                $"El ancla {tipoAncla.FullName} necesita el puente " +
                $"{typeof(Ways.Domain.Organizacion.PuntoVenta).FullName}, que no está mapeado en " +
                "el modelo.");

        var tablaDelPuente = puente.GetTableName()
            ?? throw new InvalidOperationException(
                $"El puente {puente.ClrType.FullName} del ancla {tipoAncla.Name} no tiene tabla " +
                "mapeada: el inventario no puede propagar el uso por la jerarquía.");

        var objetoDelPuente = StoreObjectIdentifier.Create(puente, StoreObjectType.Table)
            ?? throw new InvalidOperationException(
                $"El puente {puente.ClrType.FullName} del ancla {tipoAncla.Name} no resuelve a un " +
                "objeto de almacenamiento de tipo tabla.");

        var fksHaciaElAncla = ancla.GetReferencingForeignKeys()
            .Where(fk => fk.DeclaringEntityType == puente)
            .ToList();

        // Imposibilidad mecánica: con cero FKs no hay por dónde puentear y con dos no hay forma de
        // elegir sin adivinar. Las dos direcciones son inaceptables, así que se tira en CI (N1).
        if (fksHaciaElAncla.Count != 1)
        {
            throw new InvalidOperationException(
                $"{tablaDelPuente} declara {fksHaciaElAncla.Count} FKs hacia {tipoAncla.Name} y el " +
                "inventario necesita exactamente una para armar el puente de uso.");
        }

        var fkDelPuente = fksHaciaElAncla[0];
        var origen = DescribirFk(fkDelPuente);

        var columnasHaciaElAncla = fkDelPuente.Properties
            .Select(p => p.GetColumnName(objetoDelPuente)
                ?? throw new InvalidOperationException(
                    $"La FK ({origen}) del puente {puente.ClrType.FullName} hacia " +
                    $"{tipoAncla.Name} tiene la propiedad {p.Name} sin columna en {tablaDelPuente}."))
            .ToList();

        var propiedadesDelAncla = fkDelPuente.PrincipalKey.Properties
            .Select(p => LeerPropiedadDelPrincipal(p, origen, puente.ClrType, tipoAncla))
            .ToList();

        var columnaPorPropiedadDelPuente = puente.GetProperties()
            .Where(p => p.GetColumnName(objetoDelPuente) is not null)
            .ToDictionary(p => p.Name, p => p.GetColumnName(objetoDelPuente)!, StringComparer.Ordinal);

        var esquemaDelPuente = puente.GetSchema() ?? EsquemaPorDefecto(ancla.Model);
        var yaCubiertas = ramas.Select(ClaveDeRama).ToHashSet(StringComparer.Ordinal);

        foreach (var hoja in InventarioCompleto(ancla.Model, puente.ClrType)
            .Where(rama => rama.Clasificacion is not ClasificacionDeDependiente.Excluido
                && rama.Puente is null))
        {
            var columnasDeUnion = hoja.PropiedadesDelPrincipal
                .Select(propiedad => columnaPorPropiedadDelPuente.TryGetValue(propiedad, out var columna)
                    ? columna
                    : throw new InvalidOperationException(
                        $"La rama {hoja.Tabla} ({string.Join(',', hoja.Columnas)}) del ancla " +
                        $"{puente.ClrType.Name} se apoya en la propiedad {propiedad}, que no " +
                        $"resuelve columna en {tablaDelPuente}: el puente de uso de " +
                        $"{tipoAncla.Name} no se puede armar."))
                .ToList();

            var rama = new RamaDeUso(
                hoja.Esquema,
                hoja.Tabla,
                hoja.Columnas,
                propiedadesDelAncla,
                hoja.Clasificacion,
                new PuenteDeUso(esquemaDelPuente, tablaDelPuente, columnasDeUnion, columnasHaciaElAncla));

            if (yaCubiertas.Add(ClaveDeRama(rama)))
            {
                ramas.Add(rama);
            }
        }
    }

    private static string ClaveDeRama(RamaDeUso rama) =>
        $"{rama.Tabla}|{string.Join(',', rama.Columnas)}|{rama.Puente?.Tabla}";

    private static RamaDeUso ConstruirRama(IForeignKey fk, Type tipoAncla)
    {
        var dependiente = fk.DeclaringEntityType;
        var origen = DescribirFk(fk);

        var tabla = dependiente.GetTableName()
            ?? throw new InvalidOperationException(
                $"El tipo dependiente {dependiente.ClrType.FullName} referencia a " +
                $"{tipoAncla.Name} por ({origen}) pero no tiene tabla mapeada: " +
                "el inspector de uso no puede interrogarlo.");

        var objeto = StoreObjectIdentifier.Create(dependiente, StoreObjectType.Table)
            ?? throw new InvalidOperationException(
                $"El tipo dependiente {dependiente.ClrType.FullName} referencia a " +
                $"{tipoAncla.Name} por ({origen}) pero no resuelve a un objeto de " +
                "almacenamiento de tipo tabla.");

        var columnas = fk.Properties
            .Select(p => p.GetColumnName(objeto)
                ?? throw new InvalidOperationException(
                    $"La FK ({origen}) de {dependiente.ClrType.FullName} hacia " +
                    $"{tipoAncla.Name} tiene la propiedad {p.Name} sin columna en {tabla}."))
            .ToList();

        var propiedadesDelPrincipal = fk.PrincipalKey.Properties
            .Select(p => LeerPropiedadDelPrincipal(p, origen, dependiente.ClrType, tipoAncla))
            .ToList();

        return new RamaDeUso(
            dependiente.GetSchema() ?? EsquemaPorDefecto(dependiente.Model),
            tabla,
            columnas,
            propiedadesDelPrincipal,
            Clasificar(dependiente, objeto, origen, tipoAncla));
    }

    private static ClasificacionDeDependiente Clasificar(
        IEntityType dependiente, StoreObjectIdentifier objeto, string origen, Type tipoAncla)
    {
        // Orden fijo: carve-out -> marcado -> sin marca. Total por construcción.
        if (Excluidos.Contains(dependiente.ClrType))
        {
            return ClasificacionDeDependiente.Excluido;
        }

        var heredaDeEntidadBase = typeof(EntidadBase).IsAssignableFrom(dependiente.ClrType);

        var llevaMarca = dependiente.GetProperties()
            .Any(p => p.GetColumnName(objeto) == ColumnaDeMarcaTemporal);

        // Imposibilidad mecánica: hereda de EntidadBase (la convención del proyecto dice que
        // TODA tabla lleva created_at) y sin embargo la columna no se resuelve. Silenciarlo lo
        // degradaría a SinMarca, que sobre-bloquearía a todo tenant recién aprovisionado sin
        // que nadie se enterara. Lo tira N1 en CI, nunca un request.
        if (!llevaMarca && heredaDeEntidadBase)
        {
            throw new InvalidOperationException(
                $"El tipo dependiente {dependiente.ClrType.FullName} referencia a " +
                $"{tipoAncla.Name} por ({origen}), hereda de {nameof(EntidadBase)} y " +
                $"no resuelve la columna {ColumnaDeMarcaTemporal} en " +
                $"{dependiente.GetTableName()}.");
        }

        // Design sección A, balde 2: AMBAS condiciones. Una tabla con created_at que NO hereda de
        // EntidadBase no comparte la convención de estampado del proyecto, así que su marca no es
        // comparable contra el instante del ancla: cae a SinMarca (existencia), que SOBRE-bloquea
        // — el lado seguro.
        return llevaMarca && heredaDeEntidadBase
            ? ClasificacionDeDependiente.Marcado
            : ClasificacionDeDependiente.SinMarca;
    }

    private static string LeerPropiedadDelPrincipal(
        IProperty propiedad, string origen, Type tipoDependiente, Type tipoAncla)
    {
        // Imposibilidad mecánica: el ancla llega al inspector como una instancia ya cargada, así
        // que una propiedad shadow o ajena al tipo ancla no se puede leer de ella.
        var accesible = propiedad.PropertyInfo is not null
            && propiedad.DeclaringType.ClrType.IsAssignableFrom(tipoAncla);

        return accesible
            ? propiedad.Name
            : throw new InvalidOperationException(
                $"La rama ({origen}) de {tipoDependiente.FullName} hacia " +
                $"{tipoAncla.Name} depende de la propiedad principal {propiedad.Name}, que no " +
                $"es legible desde {tipoAncla.FullName}.");
    }

    private static string EsquemaPorDefecto(IModel modelo) => modelo.GetDefaultSchema() ?? "public";

    private static string DescribirFk(IForeignKey fk) =>
        $"{fk.DeclaringEntityType.ClrType.Name}.{string.Join('+', fk.Properties.Select(p => p.Name))}";
}
