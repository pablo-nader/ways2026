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
/// Una rama del inspector: la tabla dependiente, las columnas de su FK zipeadas con las
/// propiedades del principal que las alimentan, y el balde que decide su predicado.
/// </summary>
/// <param name="Esquema">Esquema de la tabla dependiente. Sale de la metadata de EF; el
/// renderizador lo necesita porque el walk es puro (D2) y no le pasa el <see cref="IModel"/>.</param>
/// <param name="Tabla">Nombre pelado de la tabla. Es también la etiqueta que
/// <c>InspectorDeUso</c> devuelve al llamador.</param>
/// <param name="Columnas">Columnas dependientes de la FK, en el orden de
/// <c>fk.Properties</c>.</param>
/// <param name="PropiedadesDelPrincipal">Propiedades del principal, zipeadas 1 a 1 con
/// <paramref name="Columnas"/>. Una FK compuesta <c>(id, id_tenant)</c> o de clave alternativa
/// sale de acá sin ningún caso especial.</param>
public sealed record RamaDeUso(
    string Esquema,
    string Tabla,
    IReadOnlyList<string> Columnas,
    IReadOnlyList<string> PropiedadesDelPrincipal,
    ClasificacionDeDependiente Clasificacion)
{
    public bool UsaAncla => Clasificacion is ClasificacionDeDependiente.Marcado;
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
/// <see cref="IEntityType.GetReferencingForeignKeys"/> es la ÚNICA fuente del conjunto de
/// dependientes: no hay lista escrita a mano de tablas que revisar, así que una tabla que una
/// etapa futura agregue entra sola. La clasificación es TOTAL por construcción — ningún
/// <c>else</c> puede tirar en runtime — y las tres imposibilidades MECÁNICAS que sí tiran
/// (<see cref="InvalidOperationException"/> nombrando el tipo CLR y la FK) son fallas de
/// build-time que ejecuta N1 en CI, nunca un 500 en producción sobre un intento de baja.
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
    /// TODA FK que referencia al ancla, carve-outs incluidos, en orden determinístico
    /// (tabla, luego columnas). Es la fuente del golden N3: un carve-out que se agregue o se
    /// saque cambia una línea del archivo en vez de desaparecer sin dejar rastro.
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

        ramas.Sort(static (a, b) =>
        {
            var porTabla = string.CompareOrdinal(a.Tabla, b.Tabla);
            return porTabla != 0
                ? porTabla
                : string.CompareOrdinal(string.Join(',', a.Columnas), string.Join(',', b.Columnas));
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

    private static RamaDeUso ConstruirRama(IForeignKey fk, Type tipoAncla)
    {
        var dependiente = fk.DeclaringEntityType;

        var tabla = dependiente.GetTableName()
            ?? throw new InvalidOperationException(
                $"El tipo dependiente {dependiente.ClrType.FullName} referencia a " +
                $"{tipoAncla.Name} por ({DescribirFk(fk)}) pero no tiene tabla mapeada: " +
                "el inspector de uso no puede interrogarlo.");

        var objeto = StoreObjectIdentifier.Create(dependiente, StoreObjectType.Table)
            ?? throw new InvalidOperationException(
                $"El tipo dependiente {dependiente.ClrType.FullName} referencia a " +
                $"{tipoAncla.Name} por ({DescribirFk(fk)}) pero no resuelve a un objeto de " +
                "almacenamiento de tipo tabla.");

        var columnas = fk.Properties
            .Select(p => p.GetColumnName(objeto)
                ?? throw new InvalidOperationException(
                    $"La FK ({DescribirFk(fk)}) de {dependiente.ClrType.FullName} hacia " +
                    $"{tipoAncla.Name} tiene la propiedad {p.Name} sin columna en {tabla}."))
            .ToList();

        var propiedadesDelPrincipal = fk.PrincipalKey.Properties
            .Select(p => LeerPropiedadDelPrincipal(p, fk, tipoAncla))
            .ToList();

        return new RamaDeUso(
            dependiente.GetSchema() ?? EsquemaPorDefecto(dependiente.Model),
            tabla,
            columnas,
            propiedadesDelPrincipal,
            Clasificar(dependiente, objeto, fk, tipoAncla));
    }

    private static ClasificacionDeDependiente Clasificar(
        IEntityType dependiente, StoreObjectIdentifier objeto, IForeignKey fk, Type tipoAncla)
    {
        // Orden fijo: carve-out -> marcado -> sin marca. Total por construcción.
        if (Excluidos.Contains(dependiente.ClrType))
        {
            return ClasificacionDeDependiente.Excluido;
        }

        var llevaMarca = dependiente.GetProperties()
            .Any(p => p.GetColumnName(objeto) == ColumnaDeMarcaTemporal);

        // Imposibilidad mecánica: hereda de EntidadBase (la convención del proyecto dice que
        // TODA tabla lleva created_at) y sin embargo la columna no se resuelve. Silenciarlo lo
        // degradaría a SinMarca, que sobre-bloquearía a todo tenant recién aprovisionado sin
        // que nadie se enterara. Lo tira N1 en CI, nunca un request.
        if (!llevaMarca && typeof(EntidadBase).IsAssignableFrom(dependiente.ClrType))
        {
            throw new InvalidOperationException(
                $"El tipo dependiente {dependiente.ClrType.FullName} referencia a " +
                $"{tipoAncla.Name} por ({DescribirFk(fk)}), hereda de {nameof(EntidadBase)} y " +
                $"no resuelve la columna {ColumnaDeMarcaTemporal} en " +
                $"{dependiente.GetTableName()}.");
        }

        return llevaMarca ? ClasificacionDeDependiente.Marcado : ClasificacionDeDependiente.SinMarca;
    }

    private static string LeerPropiedadDelPrincipal(IProperty propiedad, IForeignKey fk, Type tipoAncla)
    {
        // Imposibilidad mecánica: el ancla llega al inspector como una instancia ya cargada, así
        // que una propiedad shadow o ajena al tipo ancla no se puede leer de ella.
        var accesible = propiedad.PropertyInfo is not null
            && propiedad.DeclaringType.ClrType.IsAssignableFrom(tipoAncla);

        return accesible
            ? propiedad.Name
            : throw new InvalidOperationException(
                $"La FK ({DescribirFk(fk)}) de {fk.DeclaringEntityType.ClrType.FullName} hacia " +
                $"{tipoAncla.Name} depende de la propiedad principal {propiedad.Name}, que no " +
                $"es legible desde {tipoAncla.FullName}.");
    }

    private static string EsquemaPorDefecto(IModel modelo) => modelo.GetDefaultSchema() ?? "public";

    private static string DescribirFk(IForeignKey fk) =>
        $"{fk.DeclaringEntityType.ClrType.Name}.{string.Join('+', fk.Properties.Select(p => p.Name))}";
}
