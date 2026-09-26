namespace Ways.Domain.Ofertas;

/// <summary>Un escalón de cantidad ya resuelto: desde <see cref="CantidadDesde"/> unidades (y
/// hasta el siguiente escalón, si hay), la línea resuelve a <see cref="Resolucion"/>. Solo el
/// resultado viaja, no la oferta que lo disparó: un escalón puede nacer de varias ofertas con el
/// mismo umbral (el umbral es el eje, no la oferta).</summary>
public readonly record struct EscalonResuelto(decimal CantidadDesde, PrecioConOfertas Resolucion);

/// <summary>La curva completa de una línea: la resolución a SU PROPIA cantidad
/// (<see cref="Base"/>) más los escalones que la siguen. Las dos cosas viajan juntas porque el
/// colapso de <see cref="EscalonesDeCantidad.Colapsar"/> necesita la base como primer punto de
/// comparación y el llamador la necesita igual para el resultado plano: devolverlas de a una
/// obligaba a resolverla dos veces (judgment-day ronda 1, acordado por ambos jueces), y pedirla
/// por parámetro habría abierto la puerta a pasar la base de OTRA línea sin que nada lo note.</summary>
public readonly record struct CurvaDeCantidad(PrecioConOfertas Base, IReadOnlyList<EscalonResuelto> Escalones);

/// <summary>
/// Tabla de escalones de cantidad de UNA línea (stage-pos-venta-offline: ofertas por volumen en la
/// instantánea offline) — pura, sin base de datos, mismo criterio que
/// <see cref="ResolvedorDeOfertas"/>, del que es un consumidor: acá NO se reimplementa ni el
/// matching (<see cref="ResolvedorDeOfertas.Coincide"/>) ni la aritmética
/// (<see cref="ResolvedorDeOfertas.Resolver"/>).
///
/// <para>Existe porque el dispositivo offline no vuelve a evaluar ofertas nunca (decisión del
/// dueño, rechazada explícitamente la idea de reimplementar el motor en TypeScript): en vez de un
/// único precio congelado a cantidad unitaria — que dejaba afuera toda oferta con
/// <c>cantidad_minima &gt; 1</c> — el servidor le manda la CURVA de precio por cantidad, ya
/// resuelta por el motor real. El dispositivo solo elige el último escalón cuyo umbral es &lt;= la
/// cantidad del carrito.</para>
///
/// <para><see cref="DescubrirUmbrales"/> y <see cref="Colapsar"/> son públicos (y no detalles
/// privados de <see cref="Construir"/>) porque son las dos unidades que los tests tienen que poder
/// matar por separado: pasando por <see cref="Construir"/>, el colapso ABSORBE cualquier umbral
/// espurio que el descubrimiento deje entrar, así que un test de punta a punta no puede distinguir
/// un descubrimiento correcto de uno roto (mutation-proof-tests regla 3: rutear por debajo del
/// confound). El repo no usa <c>InternalsVisibleTo</c> en ningún lado (mismo criterio que
/// <c>InspectorDeUso.Renderizar</c>/<c>IEsperador</c>).</para>
/// </summary>
public static class EscalonesDeCantidad
{
    /// <summary>Punto de entrada único de producción: resuelve la línea a su propia cantidad,
    /// descubre los umbrales, resuelve en cada uno con el motor real y colapsa los que no cambian
    /// nada. <see cref="CurvaDeCantidad.Escalones"/> sale ascendente por
    /// <see cref="EscalonResuelto.CantidadDesde"/> y NUNCA incluye la cantidad de la propia línea:
    /// esa es <see cref="CurvaDeCantidad.Base"/> (para la instantánea, los campos planos de
    /// cantidad 1). La base se resuelve UNA sola vez y se devuelve junto con los escalones, así el
    /// llamador no la vuelve a calcular para el resultado plano.</summary>
    public static CurvaDeCantidad Construir(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas)
    {
        var resolucionDeLaLinea = ResolvedorDeOfertas.Resolver(linea, candidatas);

        var umbrales = DescubrirUmbrales(linea, candidatas);
        if (umbrales.Count == 0)
        {
            return new CurvaDeCantidad(resolucionDeLaLinea, []);
        }

        var candidatos = new List<EscalonResuelto>(umbrales.Count);
        foreach (var umbral in umbrales)
        {
            candidatos.Add(new EscalonResuelto(
                umbral, ResolvedorDeOfertas.Resolver(linea with { Cantidad = umbral }, candidatas)));
        }

        return new CurvaDeCantidad(resolucionDeLaLinea, Colapsar(resolucionDeLaLinea, candidatos));
    }

    /// <summary>
    /// Las cantidades en las que el resultado de <paramref name="linea"/> PUEDE cambiar, distintas
    /// y ascendentes.
    ///
    /// <para>Umbral candidato = <c>cantidad_minima</c> ESTRICTAMENTE mayor a
    /// <see cref="LineaAResolver.Cantidad"/>. Un umbral menor o igual ya está contemplado en la
    /// resolución de la propia línea, y emitirlo sería peor que redundante: el dispositivo elige el
    /// ÚLTIMO escalón cuyo umbral entra en la cantidad, así que un escalón por debajo de la
    /// cantidad ya resuelta le haría cobrar el precio de MENOS unidades. Para la instantánea (que
    /// resuelve a cantidad 1) esto es exactamente "todo umbral &gt; 1".</para>
    ///
    /// <para>Que un umbral <c>T</c> califique se decide con <see cref="ResolvedorDeOfertas.Coincide"/>
    /// evaluado EN <c>T</c>, nunca con una copia del matching: a cantidad <c>T</c> el eje de
    /// cantidad pasa por definición (<c>T &gt;= T</c>), así que un <c>true</c> prueba que TODOS los
    /// otros ejes (alcance, lista, día de semana, ventana de fecha, ventana de hora) matchean en el
    /// momento de esta línea. Por eso una oferta por volumen fuera de su ventana, o de otro
    /// artículo, no aporta umbral. Ese filtro NO es la red de correctitud del resultado — de eso se
    /// ocupa <see cref="Colapsar"/>, que descartaría igual un umbral espurio porque no cambia nada
    /// — sino el que evita resolver, artículo por artículo, los umbrales de TODO el tenant: el lote
    /// de la instantánea trae las candidatas de todo el catálogo juntas.</para>
    /// </summary>
    public static IReadOnlyList<decimal> DescubrirUmbrales(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas)
    {
        var umbrales = new HashSet<decimal>();

        foreach (var candidata in candidatas)
        {
            if (candidata.CantidadMinima is not { } umbral || umbral <= linea.Cantidad)
            {
                continue;
            }

            if (ResolvedorDeOfertas.Coincide(linea with { Cantidad = umbral }, candidata))
            {
                umbrales.Add(umbral);
            }
        }

        return umbrales.OrderBy(u => u).ToList();
    }

    /// <summary>
    /// Descarta los escalones que no cambian nada: un umbral cuyo resultado es igual al del escalón
    /// VIGENTE (el anterior CONSERVADO, o <paramref name="resolucionDeLaLinea"/> para el primero) es
    /// puro ruido de payload. Llegando desde <see cref="Construir"/> el caso real es uno solo: la
    /// oferta del umbral PIERDE contra la base vigente (la directa que ya aplicaba, o un escalón
    /// anterior de mayor prioridad). El otro caso clásico — la oferta del umbral no matchea por
    /// fecha/hora/alcance — no llega hasta acá, lo filtra antes
    /// <see cref="DescubrirUmbrales"/>; este colapso lo descartaría igual, y eso es justamente lo
    /// que lo vuelve la red que hace que un umbral espurio nunca altere la curva.
    ///
    /// <para><paramref name="candidatos"/> tiene que venir ascendente por
    /// <see cref="EscalonResuelto.CantidadDesde"/> (lo garantiza
    /// <see cref="DescubrirUmbrales"/>): el colapso es contra el anterior conservado, que es
    /// justo el escalón que el dispositivo estaría usando al llegar a esta cantidad.</para>
    ///
    /// <para>La comparación es <see cref="PrecioConOfertas.DescuentoUnitario"/> + los ids aplicados
    /// EN ORDEN. <see cref="PrecioConOfertas.PrecioOriginal"/> es constante entre cantidades (la
    /// cantidad no entra en la resolución del precio de lista) y
    /// <see cref="PrecioConOfertas.PrecioFinal"/> es <c>original - descuento</c>, así que ninguno de
    /// los dos aporta información que el descuento no tenga ya. Los ids sí: dos escalones pueden
    /// llegar al mismo descuento con ofertas distintas, y cuáles aplicaron es dato visible del
    /// ticket.</para>
    /// </summary>
    public static IReadOnlyList<EscalonResuelto> Colapsar(
        in PrecioConOfertas resolucionDeLaLinea, IReadOnlyList<EscalonResuelto> candidatos)
    {
        var escalones = new List<EscalonResuelto>(candidatos.Count);
        var vigente = resolucionDeLaLinea;

        foreach (var candidato in candidatos)
        {
            if (MismoResultado(vigente, candidato.Resolucion))
            {
                continue;
            }

            escalones.Add(candidato);
            vigente = candidato.Resolucion;
        }

        return escalones;
    }

    private static bool MismoResultado(in PrecioConOfertas vigente, in PrecioConOfertas candidato) =>
        vigente.DescuentoUnitario == candidato.DescuentoUnitario &&
        vigente.Aplicadas.Select(a => a.IdOferta).SequenceEqual(candidato.Aplicadas.Select(a => a.IdOferta));
}
