namespace Ways.Domain.Ofertas;

/// <summary>
/// Motor de resolución (design: Technical Approach — "SQL side is deliberately dumb"; task 3.2)
/// — pura, sin base de datos, mismo criterio que <see cref="Precios.ResolvedorDePrecios"/>.
/// <see cref="Resolver"/> hace las dos cosas que el nombre promete: primero filtra
/// <paramref name="candidatas"/> con <see cref="Coincide"/> (spec: resolucion-de-ofertas /
/// Candidate Matching — ventana de vigencia, <c>cantidad_minima</c>, lista objetivo, día de
/// semana; el alcance por <c>id_empresa</c> NO se evalúa acá — <c>ServicioDeOfertas</c> ya lo
/// resolvió antes de armar la lista de candidatas de esta línea, porque
/// <see cref="LineaAResolver"/> no lleva ese dato), y recién con las que matchean aplica
/// precedencia + apilado aditivo-sobre-original (spec: Base Selection and Tie-Break,
/// Additive-Over-Original Stacking).
/// </summary>
public static class ResolvedorDeOfertas
{
    /// <summary>Punto de entrada único: matchea y resuelve una línea contra su conjunto de
    /// candidatas (design: Resolution Contract).</summary>
    public static PrecioConOfertas Resolver(in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas)
    {
        var coincidentes = new List<OfertaCandidata>(candidatas.Count);
        foreach (var candidata in candidatas)
        {
            if (Coincide(linea, candidata))
            {
                coincidentes.Add(candidata);
            }
        }

        var aplicadas = new List<(OfertaCandidata Candidata, decimal Descuento)>();

        // Copia local: el parámetro `in` de linea no se puede capturar dentro de una lambda
        // (CS1628) — precioOriginal es el único campo de linea que la aritmética necesita de
        // acá en más.
        var precioOriginal = linea.PrecioOriginal;

        // Base: la de mayor prioridad entre las NO acumulables (spec: Base Selection and
        // Tie-Break) — empate 1: mayor descuento de línea a la cantidad del umbral
        // (ClaveDeLinea); empate 2: mayor descuento unitario; empate 3: menor id_oferta. Si no
        // hay ninguna no-acumulable que matchee, no hay base (spec: "Acumulable-only candidates
        // apply with no base" — el precio original queda como base implícita).
        (OfertaCandidata Candidata, decimal Descuento, FraccionDeDescuento Clave)? candidataBase = null;
        foreach (var candidata in coincidentes)
        {
            if (candidata.Acumulable)
            {
                continue;
            }

            var descuento = DescuentoDe(candidata.Beneficio, precioOriginal);
            var clave = ClaveDeLinea(linea, candidata, descuento);
            if (candidataBase is not { } actual || GanaLaBase(candidata, descuento, clave, actual))
            {
                candidataBase = (candidata, descuento, clave);
            }
        }

        if (candidataBase is { } elegida)
        {
            aplicadas.Add((elegida.Candidata, elegida.Descuento));
        }

        // Todas las acumulables que matchean se suman, sin competir entre sí (spec: "Multiple
        // acumulables stack on the base").
        foreach (var candidata in coincidentes.Where(c => c.Acumulable))
        {
            aplicadas.Add((candidata, DescuentoDe(candidata.Beneficio, precioOriginal)));
        }

        var descuentoTotal = Math.Clamp(aplicadas.Sum(a => a.Descuento), 0m, precioOriginal);
        var precioFinal = precioOriginal - descuentoTotal;

        var reportadas = aplicadas
            .OrderByDescending(a => a.Candidata.Prioridad)
            .ThenBy(a => a.Candidata.Id)
            .Select(a => new OfertaAplicada(
                a.Candidata.Id, a.Candidata.Nombre, a.Descuento,
                a.Candidata.SoloMultiplos ? a.Candidata.CantidadMinima : null,
                a.Candidata.SoloMultiplos && a.Candidata.Alcance.IdFamilia is not null))
            .ToList();

        return new PrecioConOfertas(linea.PrecioOriginal, precioFinal, descuentoTotal, reportadas);
    }

    private static bool GanaLaBase(
        OfertaCandidata candidata, decimal descuento, FraccionDeDescuento clave,
        (OfertaCandidata Candidata, decimal Descuento, FraccionDeDescuento Clave) actual)
    {
        if (candidata.Prioridad != actual.Candidata.Prioridad)
        {
            return candidata.Prioridad > actual.Candidata.Prioridad;
        }

        var porLinea = clave.CompareTo(actual.Clave);
        if (porLinea != 0)
        {
            return porLinea > 0;
        }

        if (descuento != actual.Descuento)
        {
            return descuento > actual.Descuento;
        }

        return candidata.Id < actual.Candidata.Id;
    }

    /// <summary>
    /// El descuento de línea de <paramref name="candidata"/> por unidad de la línea, a la cantidad de su umbral
    /// (<see cref="CantidadDelUmbral"/>): una oferta común da <paramref name="descuento"/>; una "solo múltiplos" de N da
    /// <c>descuento × N × floor(Q / N) / Q</c>, con Q la cantidad del umbral. Para una oferta de artículo, grupo o
    /// categoría eso es exactamente su descuento de línea dividido la cantidad; para una de familia es su parte
    /// proporcional del descuento de la familia (el reparto real entre las líneas lo hace <see cref="CoberturaDeOfertas"/>).
    /// No anticipa las unidades que consuma un combo.
    /// </summary>
    internal static FraccionDeDescuento ClaveDeLinea(in LineaAResolver linea, OfertaCandidata candidata, decimal descuento)
    {
        if (MultiploDe(candidata) is not { } multiplo)
        {
            return new FraccionDeDescuento(descuento, 1m);
        }

        var cantidad = CantidadDelUmbral(linea, candidata);
        return cantidad <= 0m
            ? new FraccionDeDescuento(0m, 1m)
            : new FraccionDeDescuento(descuento * multiplo * Math.Floor(cantidad / multiplo), cantidad);
    }

    /// <summary>El tamaño de grupo de una oferta "solo múltiplos" (su cantidad mínima), o <c>null</c> si cubre todas las
    /// unidades.</summary>
    internal static decimal? MultiploDe(OfertaCandidata candidata) =>
        candidata.SoloMultiplos && candidata.CantidadMinima is { } minima && minima > 0m ? minima : null;

    /// <summary>La cantidad contra la que se mide <c>cantidad_minima</c>: la de la familia para una oferta de familia, la
    /// de la línea para el resto.</summary>
    internal static decimal CantidadDelUmbral(in LineaAResolver linea, OfertaCandidata candidata) =>
        candidata.Alcance.IdFamilia is not null ? linea.CantidadDeFamilia : linea.Cantidad;

    /// <summary>El descuento unitario que el motor le calcula a <paramref name="candidata"/> sobre
    /// <paramref name="original"/>.</summary>
    internal static decimal DescuentoUnitario(OfertaCandidata candidata, decimal original) =>
        DescuentoDe(candidata.Beneficio, original);

    /// <summary>Spec: resolucion-de-ofertas / Candidate Matching — TODOS los ejes seteados
    /// tienen que matchear (AND), cada eje NULL/vacío no restringe (spec: ofertas / Vigencia
    /// Window Semantics, Multi-Lista Targeting via ofertas_listas). El alcance
    /// (articulo/grupo/categoria/familia) usa <see cref="LineaAResolver.IdsCategorias"/> — ya expandido
    /// a la cadena de ancestros por el llamador (<see cref="CadenaDeCategorias"/>), así que un
    /// <c>Contains</c> plano alcanza para el match jerárquico.</summary>
    public static bool Coincide(in LineaAResolver linea, OfertaCandidata candidata)
    {
        if (!CoincideAlcance(linea, candidata.Alcance))
        {
            return false;
        }

        if (candidata.ListasObjetivo.Count > 0 && !candidata.ListasObjetivo.Contains(linea.IdListaPrecio))
        {
            return false;
        }

        if (candidata.DiasSemana.Count > 0 && !candidata.DiasSemana.Contains(linea.DiaSemana))
        {
            return false;
        }

        if (candidata.FechaDesde is { } fechaDesde && linea.Fecha < fechaDesde)
        {
            return false;
        }

        if (candidata.FechaHasta is { } fechaHasta && linea.Fecha > fechaHasta)
        {
            return false;
        }

        if (candidata.HoraDesde is { } horaDesde && linea.Hora < horaDesde)
        {
            return false;
        }

        if (candidata.HoraHasta is { } horaHasta && linea.Hora > horaHasta)
        {
            return false;
        }

        if (candidata.CantidadMinima is not { } cantidadMinima)
        {
            return true;
        }

        return CantidadDelUmbral(linea, candidata) >= cantidadMinima;
    }

    private static bool CoincideAlcance(in LineaAResolver linea, AlcanceDeOferta alcance)
    {
        if (alcance.IdArticulo is { } idArticulo)
        {
            return idArticulo == linea.IdArticulo;
        }

        if (alcance.IdGrupo is { } idGrupo)
        {
            return linea.IdGrupo == idGrupo;
        }

        if (alcance.IdFamilia is { } idFamilia)
        {
            return linea.IdFamilia == idFamilia;
        }

        var idCategoria = alcance.IdCategoria!.Value;
        return linea.IdsCategorias.Contains(idCategoria);
    }

    /// <summary>Design: arithmetic table (binding, proposal decision 1) — cada beneficio se
    /// calcula independientemente contra <paramref name="original"/>, redondeado a 2 decimales
    /// <see cref="MidpointRounding.AwayFromZero"/> (mismo criterio de punto de venta que
    /// <see cref="Precios.ResolvedorDePrecios"/>) y clampeado a <c>[0, original]</c> — una oferta
    /// nunca puede subir el precio (<c>precio_unitario &gt; original</c> da 0, no un descuento
    /// negativo).</summary>
    private static decimal DescuentoDe(BeneficioDeOferta beneficio, decimal original)
    {
        var crudo = beneficio.Porcentaje is { } porcentaje ? original * porcentaje / 100m
            : beneficio.ImporteFijo is { } importeFijo ? importeFijo
            : beneficio.PrecioUnitario is { } precioUnitario ? original - precioUnitario
            : throw new InvalidOperationException(
                "BeneficioDeOferta sin beneficio seteado — invariante de ReglaDeOfertas.LeerBeneficio violado.");

        var redondeado = Math.Round(crudo, 2, MidpointRounding.AwayFromZero);

        return Math.Clamp(redondeado, 0m, original);
    }
}

/// <summary>Un cociente <c>Numerador / Denominador</c> (denominador positivo) que se compara sin dividir, multiplicando en
/// cruz: así dos claves de <see cref="ResolvedorDeOfertas.ClaveDeLinea"/> empatan solo si empatan exactamente.</summary>
internal readonly record struct FraccionDeDescuento(decimal Numerador, decimal Denominador)
    : IComparable<FraccionDeDescuento>
{
    public int CompareTo(FraccionDeDescuento otra) =>
        (Numerador * otra.Denominador).CompareTo(otra.Numerador * Denominador);
}
