using System.Collections.Frozen;

namespace Ways.Application.Organizacion;

/// <summary>
/// Cómo se NOMBRA, en castellano y para el operador, la tabla que bloqueó una baja
/// (stage-20 decisión 9). <c>InspectorDeUso</c> devuelve el nombre físico de la tabla —
/// <c>comprobantes_venta</c>— y esto lo convierte en la palabra que el operador entiende
/// —"ventas"—.
///
/// NO ES LA LISTA A MANO QUE B4 PROHÍBE, y la diferencia es sustancial: acá no se decide NADA
/// sobre el veredicto. El veredicto ya lo tomó <c>InventarioDeDependientes</c> recorriendo la
/// metadata de EF; este diccionario decide solamente CÓMO SE REDACTA un bloqueo ya decidido.
/// Una entrada que falte cuesta una frase más vaga (<see cref="Generica"/>), nunca un veredicto
/// equivocado: por eso puede quedarse corto sin fallar abierto, mientras que una lista de tablas
/// bloqueantes escrita a mano volvería borrable a una entidad en uso apenas alguien olvidara una
/// tabla.
///
/// Lo que queda deliberadamente SIN etiqueta es lo mecánico —contadores de numeración, la tabla
/// puente de ofertas—: son filas que el cliente nunca "cargó" con ese nombre, así que nombrarlas
/// confundiría más que la frase genérica. <c>arqueos_turno</c> SALIÓ de esa lista en judgment-day
/// ronda 1 (hallazgo C7): el arqueo de cierre es una operación que el cajero hace y ve con ese
/// nombre en pantalla (<c>CierreDeCaja.tsx</c>/<c>CajaZ.tsx</c>), no un contador mecánico, y decirle
/// "porque tiene datos cargados" a un tenant bloqueado por un arqueo lo manda a buscar a ciegas.
/// </summary>
public static class EtiquetasDeTablas
{
    /// <summary>Frase de fallback cuando la tabla que bloqueó no tiene etiqueta propia.</summary>
    public const string Generica = "datos cargados";

    private static readonly FrozenDictionary<string, string> PorTabla =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["areas"] = "áreas",
            ["arqueos_turno"] = "arqueos de caja",
            ["articulos"] = "artículos",
            ["articulos_empresas"] = "artículos habilitados",
            ["categorias"] = "categorías",
            ["certificados_fiscales"] = "certificados fiscales",
            ["clientes"] = "clientes",
            ["codigos_barra"] = "códigos de barra",
            ["comprobantes_compra"] = "compras",
            ["comprobantes_venta"] = "ventas",
            ["empresas"] = "empresas",
            ["gastos"] = "gastos",
            ["grupos"] = "grupos",
            ["items_comprobante_compra"] = "compras",
            ["items_comprobante_venta"] = "ventas",
            ["items_orden_compra"] = "órdenes de compra",
            ["items_presupuesto"] = "presupuestos",
            ["items_remito"] = "remitos",
            ["listas_precio"] = "listas de precios",
            ["lotes"] = "lotes",
            ["marcas"] = "marcas",
            ["medios_pago"] = "medios de pago",
            ["movimientos_caja"] = "movimientos de caja",
            ["movimientos_cuenta_corriente"] = "movimientos de cuenta corriente",
            ["movimientos_cuenta_corriente_proveedor"] = "movimientos de cuenta corriente de proveedores",
            ["movimientos_stock"] = "movimientos de stock",
            ["movimientos_tesoreria"] = "movimientos de tesorería",
            ["ofertas"] = "ofertas",
            ["ordenes_compra"] = "órdenes de compra",
            ["pagos_comprobante"] = "pagos",
            ["parametros"] = "parámetros",
            ["precios"] = "precios",
            ["presupuestos"] = "presupuestos",
            ["proveedores"] = "proveedores",
            ["puntos_venta"] = "puntos de venta",
            ["remitos"] = "remitos",
            ["stock"] = "stock",
            ["stock_lotes"] = "stock por lote",
            ["turnos_caja"] = "turnos de caja",
            ["usuarios"] = "usuarios",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>La etiqueta de una tabla, o <see cref="Generica"/> si no tiene una propia.</summary>
    public static string Describir(string tabla) => PorTabla.GetValueOrDefault(tabla, Generica);

    /// <summary>
    /// La descripción del bloqueo, que NO siempre es la etiqueta pelada de la hoja.
    ///
    /// Una rama PUENTEADA (design amendment de la slice 3: el uso sube por la jerarquía
    /// estructural) llega a la hoja a través de una tabla intermedia, y el inspector devuelve
    /// siempre la HOJA. Sin esto, una empresa bloqueada por un turno de caja de su punto de venta
    /// le diría al operador "tiene turnos de caja" sin ninguna pista de que la fila vive en un
    /// punto de venta: el operador buscaría el turno en la empresa y no lo encontraría. Con el
    /// puente nombrado, la frase queda "turnos de caja en sus puntos de venta".
    ///
    /// Alcanza con que ALGUNA rama de esa hoja sea puenteada (judgment-day ronda 1, hallazgo C3).
    /// La regla anterior —afirmar el puente solo cuando TODAS lo eran— degradaba a la etiqueta
    /// pelada justo cuando la hoja tenía además una rama directa (hoy: <c>parametros</c>), que es
    /// el caso donde la pista importa más, y violaba la entrada arrastrada de la slice 3 ("nunca
    /// una etiqueta pelada para un hit puenteado"). Nombrar el puente no puede desorientar al
    /// operador aunque el hit real haya venido por la rama directa: esa fila la habría reportado la
    /// etiqueta de la rama directa igual, con la misma palabra de hoja. Quedarse callado sobre el
    /// puente, en cambio, manda a buscar en el lugar equivocado.
    /// </summary>
    public static string DescribirBloqueo(string tabla, IReadOnlyList<RamaDeUso> ramasDelAncla)
    {
        ArgumentNullException.ThrowIfNull(ramasDelAncla);

        var etiqueta = Describir(tabla);

        var puenteada = ramasDelAncla
            .FirstOrDefault(rama => rama.Tabla == tabla && rama.Puente is not null);

        return puenteada is null
            ? etiqueta
            : $"{etiqueta} en sus {Describir(puenteada.Puente!.Tabla)}";
    }
}
