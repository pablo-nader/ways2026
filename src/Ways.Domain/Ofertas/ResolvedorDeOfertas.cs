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
        // Tie-Break) — empate 1: mayor descuento efectivo; empate 2: menor id_oferta. Si no hay
        // ninguna no-acumulable que matchee, no hay base (spec: "Acumulable-only candidates
        // apply with no base" — el precio original queda como base implícita).
        var noAcumulables = coincidentes.Where(c => !c.Acumulable).ToList();
        if (noAcumulables.Count > 0)
        {
            var candidataBase = noAcumulables
                .Select(c => (Candidata: c, Descuento: DescuentoDe(c.Beneficio, precioOriginal)))
                .OrderByDescending(x => x.Candidata.Prioridad)
                .ThenByDescending(x => x.Descuento)
                .ThenBy(x => x.Candidata.Id)
                .First();

            aplicadas.Add(candidataBase);
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
            .Select(a => new OfertaAplicada(a.Candidata.Id, a.Candidata.Nombre, a.Descuento))
            .ToList();

        return new PrecioConOfertas(linea.PrecioOriginal, precioFinal, descuentoTotal, reportadas);
    }

    /// <summary>Spec: resolucion-de-ofertas / Candidate Matching — TODOS los ejes seteados
    /// tienen que matchear (AND), cada eje NULL/vacío no restringe (spec: ofertas / Vigencia
    /// Window Semantics, Multi-Lista Targeting via ofertas_listas). El alcance
    /// (articulo/grupo/categoria) usa <see cref="LineaAResolver.IdsCategorias"/> — ya expandido
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

        return candidata.CantidadMinima is not { } cantidadMinima || linea.Cantidad >= cantidadMinima;
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
